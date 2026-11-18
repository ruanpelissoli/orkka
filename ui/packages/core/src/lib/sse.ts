import { ApiError, authHeaders, extractErrorMessage } from "./api";
import { getClientLocale } from "./locale";
import { WORKSPACE_HEADER, getClientWorkspace } from "./workspace";

// Same origin, like the API (see api.ts): stream URLs are relative.
const BASE_URL = "";

// ── Streaming fetch injection ────────────────────────────────────────────
// These functions read the response body incrementally (`res.body.getReader()`), which
// browsers support on the global fetch but React Native's global fetch does not (its
// bodies are buffered). Rather than polyfilling globals, a host that has a streaming
// fetch (Expo: `import { fetch } from "expo/fetch"`) installs it once at startup with
// `configureStreamingFetch`; browsers never call it and keep using the global.
type FetchLike = typeof fetch;
let streamingFetch: FetchLike | null = null;

export function configureStreamingFetch(fetchImpl: FetchLike): void {
  streamingFetch = fetchImpl;
}

function getStreamingFetch(): FetchLike {
  return streamingFetch ?? fetch;
}

/**
 * The server's own failure message, not just its status code. A 402 from the budget guard
 * carries the sentence the user has to act on ("This run reached its spend limit"); throwing a
 * bare `failed: 402` would swallow it and leave the chat showing a number. Falls back to the
 * status line when the body is not one of our problem responses (gateway HTML, empty body).
 */
async function streamError(res: Response, fallback: string): Promise<Error> {
  const body = await res.text().catch(() => "");
  return new Error(extractErrorMessage(new ApiError(res.status, body), fallback));
}

/**
 * Reads a `data: {...}\n\n` event stream to its end. `handle` returns true when the event
 * ended the turn (it already called the caller's onDone); an error it throws reaches the
 * caller's catch. A stream that closes without a terminal event still resolves via onDone,
 * so the panel never hangs on a dropped connection.
 */
async function readDataEvents(
  res: Response,
  handle: (data: Record<string, unknown>) => boolean,
  onDone: () => void
): Promise<void> {
  const reader = res.body?.getReader();
  if (!reader) {
    throw new Error("No response body");
  }

  const decoder = new TextDecoder();
  let buffer = "";

  const dispatch = (line: string): boolean => {
    const trimmed = line.trim();
    if (!trimmed.startsWith("data: ")) return false;
    // Parse inside the try, dispatch outside it: a malformed line is skipped, but an
    // error event thrown by the handler must reach the caller's catch and onError.
    let data: Record<string, unknown> | null = null;
    try {
      data = JSON.parse(trimmed.slice(6));
    } catch {
      return false; // Skip malformed JSON lines
    }
    return !!data && handle(data);
  };

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";

    for (const line of lines) {
      if (dispatch(line)) return;
    }
  }

  // Process any remaining buffer
  if (dispatch(buffer)) return;

  onDone();
}

export interface DesignAssetSseEvent {
  id?: string;
  name: string;
  kind: string;
  version?: number;
  url?: string;
  status?: string;
  error?: string;
}

export interface DesignAssetProgressSseEvent {
  name: string;
  kind: string;
  status: string;
  frame?: number | null;
  totalFrames?: number | null;
}

export async function streamAgentMessage(
  projectId: string,
  agentId: string,
  conversationId: string,
  content: string,
  token: string,
  onChunk: (text: string) => void,
  onDone: () => void,
  onError: (error: Error) => void,
  onThinking?: (text: string) => void,
  onDiagram?: (diagram: { id: string; title: string }) => void,
  onTask?: (task: { id: string; number: number; title: string }) => void,
  onDocument?: (document: { id: string; title: string }) => void,
  onTitle?: (title: string) => void,
  onDesignAsset?: (asset: DesignAssetSseEvent) => void,
  onDesignAssetProgress?: (progress: DesignAssetProgressSseEvent) => void,
  /** Where the turn was started from. Design assets the agent creates are filed here. */
  options?: { designFolderPath?: string | null }
): Promise<void> {
  try {
    const res = await getStreamingFetch()(
      `${BASE_URL}/api/projects/${projectId}/agents/${agentId}/conversations/${conversationId}/messages`,
      {
        method: "POST",
        credentials: "same-origin",
        headers: {
          ...authHeaders(token),
          "Content-Type": "application/json",
          "Accept-Language": getClientLocale(),
          ...(getClientWorkspace() ? { [WORKSPACE_HEADER]: getClientWorkspace()! } : {}),
        },
        body: JSON.stringify({
          content,
          designFolderPath: options?.designFolderPath ?? null,
        }),
      }
    );

    if (!res.ok) {
      throw await streamError(res, `Agent message failed: ${res.status}`);
    }

    function processEvent(data: Record<string, unknown>) {
      // The server reports a failed turn as an `error` event (a workspace that could not be
      // prepared, a run that died) — usually paired with `done`. Before this it was dropped
      // on the floor and the panel just went quiet: onError is the only honest outcome.
      if (typeof data.error === "string" && data.error) {
        throw new Error(data.error);
      }
      if (data.done) {
        onDone();
        return true;
      }
      if (data.thinking && onThinking) {
        onThinking(data.thinking as string);
      }
      if (data.diagram && onDiagram) {
        onDiagram(data.diagram as { id: string; title: string });
      }
      if (data.task && onTask) {
        onTask(data.task as { id: string; number: number; title: string });
      }
      if (data.document && onDocument) {
        onDocument(data.document as { id: string; title: string });
      }
      if (data.designAsset && onDesignAsset) {
        onDesignAsset(data.designAsset as DesignAssetSseEvent);
      }
      if (data.designAssetProgress && onDesignAssetProgress) {
        onDesignAssetProgress(
          data.designAssetProgress as DesignAssetProgressSseEvent
        );
      }
      if (data.title && onTitle) {
        onTitle(data.title as string);
      }
      if (data.text) {
        onChunk(data.text as string);
      }
      return false;
    }

    await readDataEvents(res, processEvent, onDone);
  } catch (err) {
    onError(err instanceof Error ? err : new Error(String(err)));
  }
}

/** Callbacks of a task "Discuss" turn — the same event contract as the Team agent chat. */
export interface TaskChatStreamCallbacks {
  onChunk: (text: string) => void;
  /** `cancelled` = the reply was stopped (Stop button); whatever streamed is kept. */
  onDone: (cancelled: boolean) => void;
  onError: (error: Error) => void;
  onThinking?: (text: string) => void;
  /** A tool the agent is running while it explores the repo (the server sends its label). */
  onToolUse?: (label: string) => void;
}

/**
 * One turn of the task's "Discuss" chat: POST /api/tasks/{taskId}/chat answers with the Team
 * agent chat's event stream (`thinking`, `text`, `tool_use`, `error`, `done`). A 409 before
 * the stream (a reply already running) and an `{error}` event on it (the runner is offline)
 * both reach onError with the server's own sentence.
 */
export async function streamTaskChatMessage(
  taskId: string,
  content: string,
  agentId: string | null | undefined,
  token: string,
  callbacks: TaskChatStreamCallbacks
): Promise<void> {
  const { onChunk, onDone, onError, onThinking, onToolUse } = callbacks;
  try {
    const res = await getStreamingFetch()(`${BASE_URL}/api/tasks/${taskId}/chat`, {
      method: "POST",
      credentials: "same-origin",
      headers: {
        ...authHeaders(token),
        "Content-Type": "application/json",
        "Accept-Language": getClientLocale(),
        ...(getClientWorkspace() ? { [WORKSPACE_HEADER]: getClientWorkspace()! } : {}),
      },
      body: JSON.stringify(agentId ? { content, agentId } : { content }),
    });

    if (!res.ok) {
      throw await streamError(res, `Task chat message failed: ${res.status}`);
    }

    await readDataEvents(
      res,
      (data) => {
        if (typeof data.error === "string" && data.error) {
          throw new Error(data.error);
        }
        if (data.done) {
          onDone(data.cancelled === true);
          return true;
        }
        if (typeof data.thinking === "string" && data.thinking && onThinking) {
          onThinking(data.thinking);
        }
        if (typeof data.tool_use === "string" && data.tool_use && onToolUse) {
          onToolUse(data.tool_use);
        }
        if (typeof data.text === "string" && data.text) {
          onChunk(data.text);
        }
        return false;
      },
      () => onDone(false)
    );
  } catch (err) {
    onError(err instanceof Error ? err : new Error(String(err)));
  }
}
