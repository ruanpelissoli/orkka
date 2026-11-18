import { describe, it, expect, beforeEach } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { mockApi, mockInvalidateApiCache } from "../mocks/api";
import { getDataVersion } from "@/lib/data-refresh";
import {
  deleteProject,
  deleteTask,
  moveTaskLane,
  sendMessage,
  startInteractiveSession,
  submitTask,
} from "@/lib/actions";

/**
 * lib/actions runs in the browser: the session cookie authenticates (empty token), a write
 * refreshes the page data (refreshData), and nothing navigates — redirecting actions return
 * the target and the caller routes.
 */
describe("client actions", () => {
  beforeEach(() => {
    mockInvalidateApiCache.mockClear();
  });

  it("calls the API with the empty (cookie) token and refreshes the page data", async () => {
    mockApi.tasks.moveLane.mockResolvedValueOnce(undefined);
    const before = getDataVersion();

    await moveTaskLane("task-1", "CodeReview");

    expect(mockApi.tasks.moveLane).toHaveBeenCalledWith("task-1", "CodeReview", "");
    expect(getDataVersion()).toBe(before + 1);
    expect(mockInvalidateApiCache).toHaveBeenCalled();
  });

  it("does not refresh after a write that changes no page data", async () => {
    mockApi.messages.send.mockResolvedValueOnce(undefined);
    const before = getDataVersion();

    await sendMessage("task-1", "hello");

    expect(getDataVersion()).toBe(before);
  });

  it("returns an expected failure instead of throwing, and does not refresh", async () => {
    mockApi.tasks.submit.mockRejectedValueOnce(new Error("no AI provider connected"));
    const before = getDataVersion();

    const result = await submitTask("task-1");

    expect(result.error).toEqual(expect.any(String));
    expect(getDataVersion()).toBe(before);
  });

  it("returns where to go after a delete instead of redirecting", async () => {
    mockApi.tasks.delete.mockResolvedValueOnce(undefined);
    mockApi.projects.delete.mockResolvedValueOnce(undefined);

    await expect(deleteTask("task-1", "proj-1")).resolves.toBe("/p/proj-1/board");
    await expect(deleteProject("proj-1")).resolves.toBe("/dashboard");
  });

  it("returns the new session's page", async () => {
    mockApi.tasks.startInteractiveSession.mockResolvedValueOnce({ id: "t", number: 7 });

    await expect(startInteractiveSession("proj-1", "repo-1")).resolves.toEqual({
      error: null,
      path: "/p/proj-1/tasks/7",
    });

    mockApi.tasks.startInteractiveSession.mockRejectedValueOnce(new Error("nope"));
    const failed = await startInteractiveSession("proj-1", "repo-1");
    expect(failed.error).toEqual(expect.any(String));
  });

  it("never navigates", () => {
    const source = fs.readFileSync(path.resolve(__dirname, "../../lib/actions.ts"), "utf8");
    expect(source).not.toMatch(/from "(react-router|@\/i18n\/navigation|@\/lib\/navigation)"/);
  });
});
