import { createParser, type EventSourceMessage } from "eventsource-parser";

export interface SseEvent {
  event?: string;
  data: string;
  id?: string;
}

/** Iterate the SSE events of a fetch Response. */
export async function* readSse(res: Response, deadlineMs = 15 * 60_000): AsyncGenerator<SseEvent> {
  if (!res.body) throw new Error(`SSE response has no body (status ${res.status})`);
  const queue: SseEvent[] = [];
  let resolveWait: (() => void) | undefined;
  const parser = createParser({
    onEvent(ev: EventSourceMessage) {
      queue.push({ event: ev.event, data: ev.data, id: ev.id });
      resolveWait?.();
    },
  });
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  const started = Date.now();
  let finished = false;
  const pump = (async () => {
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        parser.feed(decoder.decode(value, { stream: true }));
      }
    } finally {
      finished = true;
      resolveWait?.();
    }
  })();
  for (;;) {
    while (queue.length) yield queue.shift()!;
    if (finished) break;
    if (Date.now() - started > deadlineMs) {
      await reader.cancel().catch(() => {});
      throw new Error("SSE stream exceeded its deadline");
    }
    await new Promise<void>((r) => {
      resolveWait = r;
      setTimeout(r, 1000);
    });
    resolveWait = undefined;
  }
  await pump;
}

export interface ChatTurn {
  text: string;
  error?: string;
  thinkingNotices: string[];
  eventCount: number;
}

/**
 * Consume one agent-chat SSE turn. The API emits `data: {"text": "..."}` chunks, optional
 * `{"thinking": "..."}` notices, `{"error": "..."}` and a final `{"done": true}`.
 */
export async function collectTurn(res: Response, deadlineMs = 10 * 60_000): Promise<ChatTurn> {
  let text = "";
  let error: string | undefined;
  const thinkingNotices: string[] = [];
  let eventCount = 0;
  for await (const ev of readSse(res, deadlineMs)) {
    eventCount++;
    if (!ev.data) continue;
    let payload: Record<string, unknown>;
    try {
      payload = JSON.parse(ev.data);
    } catch {
      text += ev.data;
      continue;
    }
    if (typeof payload.text === "string") text += payload.text;
    else if (typeof payload.content === "string") text += payload.content;
    if (typeof payload.thinking === "string") thinkingNotices.push(payload.thinking);
    if (typeof payload.error === "string") error = payload.error;
    if (payload.done === true) break;
  }
  return { text, error, thinkingNotices, eventCount };
}
