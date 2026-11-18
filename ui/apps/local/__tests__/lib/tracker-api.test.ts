import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { api } from "@/lib/api";

const SECRET = "ATATT3xFfGF0-super-secret-token";

describe("api.trackers connection calls", () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("POSTs a new connection with the token in the JSON body, never in the URL", async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ supported: true, ok: false, error: "bad token" }), { status: 200 })
    );

    const response = await api.trackers.addConnection(
      { kind: "jira", site: "acme.atlassian.net", email: "ruan@acme.com", token: SECRET },
      ""
    );

    expect(response).toEqual({ supported: true, ok: false, error: "bad token" });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(String(url)).toMatch(/\/api\/trackers\/connections$/);
    expect(String(url)).not.toContain(SECRET);
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      kind: "jira",
      site: "acme.atlassian.net",
      email: "ruan@acme.com",
      token: SECRET,
    });
    // Not in any header either (same-origin cookie auth: no Authorization).
    expect(JSON.stringify(init.headers ?? {})).not.toContain(SECRET);
  });

  it("DELETEs a connection by id", async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ supported: true, ok: true }), { status: 200 }));

    await api.trackers.removeConnection("01JTRACKER", "");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(String(url)).toMatch(/\/api\/trackers\/connections\/01JTRACKER$/);
    expect(init.method).toBe("DELETE");
  });
});
