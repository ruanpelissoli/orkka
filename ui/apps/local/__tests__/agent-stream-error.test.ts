import { describe, expect, it } from "vitest";
import { configureStreamingFetch, streamAgentMessage } from "@/lib/sse";

/**
 * A blocked run is the one failure the user can actually act on: the budget guard answers
 * 402 with the sentence that tells them what to do ("This run reached its spend limit"). The
 * chat must show that sentence, not the status code it arrived with.
 */
describe("streamAgentMessage error reporting", () => {
  function respondWith(status: number, body: string) {
    configureStreamingFetch(async () => new Response(body, { status }));
  }

  async function errorFrom(): Promise<string> {
    let message = "";
    await streamAgentMessage(
      "proj",
      "agent",
      "conv",
      "hello",
      "token",
      () => {},
      () => {},
      (e) => {
        message = e.message;
      }
    );
    return message;
  }

  it("surfaces the API's own message when a run hits its spend limit", async () => {
    respondWith(
      402,
      JSON.stringify({
        statusCode: 402,
        errors: { generalErrors: ["This run reached its spend limit"] },
      })
    );

    expect(await errorFrom()).toBe("This run reached its spend limit");
  });

  it("falls back to the status when the body is not one of ours", async () => {
    respondWith(502, "<html>gateway</html>");

    expect(await errorFrom()).toBe("Agent message failed: 502");
  });

  it("reports an error event from an open stream instead of ending quietly", async () => {
    // A turn whose workspace could not be prepared (or whose run died) is reported on the
    // already-open stream as `{error, done}`. That must reach onError with the sentence,
    // not collapse into a silent onDone with no bubble.
    const reason = "I couldn't get the workspace ready for this turn: clone timed out";
    const body =
      "data: " + JSON.stringify({ thinking: "Preparing the workspace" }) + "\n\n" +
      "data: " + JSON.stringify({ error: reason, done: true }) + "\n\n";
    respondWith(200, body);

    expect(await errorFrom()).toBe(reason);
  });
});
