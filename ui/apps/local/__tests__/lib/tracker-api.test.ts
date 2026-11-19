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

  it("POSTs a GitHub connection that uses the gh login with no token and its auth mode", async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ supported: true, ok: true }), { status: 200 }));

    await api.trackers.addConnection({ kind: "github", site: "acme.ghe.com", token: "", authMode: "gh" }, "");

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(String(init.body))).toEqual({
      kind: "github",
      site: "acme.ghe.com",
      token: "",
      authMode: "gh",
    });
  });

  it("asks the GitHub-only queries with the project, field and repository filter in the body", async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ supported: true, result: { ok: true, repositories: ["acme/api"] } }), {
        status: 200,
      })
    );

    const response = await api.trackers.query(
      "01JTRACKER",
      { kind: "preview", scope: "acme", boardId: "PVT_1", statusField: "Status", repoFilter: ["acme/api"] },
      ""
    );

    expect(response.result?.repositories).toEqual(["acme/api"]);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(String(url)).toMatch(/\/api\/trackers\/connections\/01JTRACKER\/query$/);
    expect(JSON.parse(String(init.body))).toEqual({
      kind: "preview",
      scope: "acme",
      boardId: "PVT_1",
      statusField: "Status",
      repoFilter: ["acme/api"],
    });
  });

  it("DELETEs a connection by id", async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ supported: true, ok: true }), { status: 200 }));

    await api.trackers.removeConnection("01JTRACKER", "");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(String(url)).toMatch(/\/api\/trackers\/connections\/01JTRACKER$/);
    expect(init.method).toBe("DELETE");
  });
});
