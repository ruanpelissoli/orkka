import { describe, expect, it } from "vitest";
import { configureStreamingFetch, streamTaskChatMessage } from "@/lib/sse";

/**
 * The task "Discuss" stream speaks the Team agent chat's event contract. These pin the
 * parsing the panel relies on: text/thinking/tool_use routing, the cancelled flag on done,
 * the server's sentence on a 409 before the stream, and the runner-offline error event.
 */
describe("streamTaskChatMessage", () => {
  function sse(...events: Record<string, unknown>[]): string {
    return events.map((e) => "data: " + JSON.stringify(e) + "\n\n").join("");
  }

  async function run(status: number, body: string) {
    const calls: { url: string; init: RequestInit }[] = [];
    configureStreamingFetch(async (url, init) => {
      calls.push({ url: String(url), init: init ?? {} });
      return new Response(body, { status });
    });
    const seen = {
      text: "",
      thinking: "",
      tools: [] as string[],
      done: [] as boolean[],
      error: null as string | null,
    };
    await streamTaskChatMessage("task-1", "What does this ticket need?", "agent-2", "", {
      onChunk: (t) => (seen.text += t),
      onThinking: (t) => (seen.thinking += t),
      onToolUse: (label) => seen.tools.push(label),
      onDone: (cancelled) => seen.done.push(cancelled),
      onError: (e) => (seen.error = e.message),
    });
    return { seen, calls };
  }

  it("posts the turn to the task's chat endpoint with the picked agent", async () => {
    const { calls } = await run(200, sse({ done: true }));
    expect(calls).toHaveLength(1);
    expect(calls[0].url).toBe("/api/tasks/task-1/chat");
    expect(calls[0].init.method).toBe("POST");
    expect(JSON.parse(String(calls[0].init.body))).toEqual({
      content: "What does this ticket need?",
      agentId: "agent-2",
    });
    expect(calls[0].init.credentials).toBe("same-origin");
  });

  it("routes thinking, tool use and text, then finishes once", async () => {
    const { seen } = await run(
      200,
      sse(
        { thinking: "Reading the ticket" },
        { tool_use: "Read src/app.ts" },
        { text: "Here is " },
        { text: "a plan." },
        { done: true }
      )
    );
    expect(seen.thinking).toBe("Reading the ticket");
    expect(seen.tools).toEqual(["Read src/app.ts"]);
    expect(seen.text).toBe("Here is a plan.");
    expect(seen.done).toEqual([false]);
    expect(seen.error).toBeNull();
  });

  it("reports a cancelled reply as done with the cancelled flag", async () => {
    const { seen } = await run(200, sse({ text: "Partial" }, { done: true, cancelled: true }));
    expect(seen.done).toEqual([true]);
    expect(seen.text).toBe("Partial");
  });

  it("surfaces the runner-offline error event instead of ending quietly", async () => {
    const reason = "Your runner is offline. Run ./orkka start and try again.";
    const { seen } = await run(200, sse({ error: reason, done: true }));
    expect(seen.error).toBe(reason);
    expect(seen.done).toEqual([]);
  });

  it("surfaces the API's sentence when a reply is already running (409)", async () => {
    const { seen } = await run(
      409,
      JSON.stringify({ statusCode: 409, errors: { generalErrors: ["A reply is already running"] } })
    );
    expect(seen.error).toBe("A reply is already running");
  });

  it("finishes when the stream closes without a done event", async () => {
    const { seen } = await run(200, sse({ text: "Hi" }));
    expect(seen.text).toBe("Hi");
    expect(seen.done).toEqual([false]);
  });
});
