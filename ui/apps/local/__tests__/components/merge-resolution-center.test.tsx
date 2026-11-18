import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import type { MergeConflictsInfo } from "@/lib/types";

import { MergeResolutionCenter, formatElapsed } from "@/components/merge/MergeResolutionCenter";

const base: MergeConflictsInfo = {
  status: "awaitingChoice",
  taskTitle: "Add checkout flow",
  summary: "Two updates changed the pricing panel.",
  files: [{ path: "src/pricing.ts", detail: "both modified", choice: null }],
  failureReason: null,
  pendingStrategy: null,
  failureKind: null,
  startedAt: null,
  queuedStrategy: null,
  queuedGuidance: null,
};

function show(info: Partial<MergeConflictsInfo>) {
  mockApi.tasks.getMergeConflicts.mockResolvedValue({ ...base, ...info });
  return render(
    <MergeResolutionCenter taskId="task-1" projectId="proj-1" taskNumber={7} />
  );
}

describe("formatElapsed", () => {
  it("reads as digits and unit letters in any locale", () => {
    expect(formatElapsed(0)).toBe("0s");
    expect(formatElapsed(45_000)).toBe("45s");
    expect(formatElapsed(125_000)).toBe("2m 05s");
    expect(formatElapsed(3_600_000)).toBe("60m 00s");
    // A clock that counts backwards is worse than no clock.
    expect(formatElapsed(-5_000)).toBe("0s");
  });
});

describe("MergeResolutionCenter", () => {
  beforeEach(() => {
    mockApi.tasks.getMergeConflicts.mockReset();
    mockApi.tasks.resolveMerge.mockReset();
    mockApi.tasks.cancelQueuedMerge.mockReset();
    mockApi.tasks.resolveMerge.mockResolvedValue(undefined);
    mockApi.tasks.cancelQueuedMerge.mockResolvedValue(undefined);
  });

  describe("awaitingChoice", () => {
    it("recommends the AI combine", async () => {
      show({ status: "awaitingChoice" });

      expect(await screen.findByText("mergePage.recommended")).toBeInTheDocument();
      expect(screen.getByText("mergePage.combineButton")).toBeInTheDocument();
      expect(screen.queryByText("mergePage.exhaustedTitle")).not.toBeInTheDocument();
    });
  });

  describe("exhausted", () => {
    it("says automation gave up, and shows why", async () => {
      show({
        status: "exhausted",
        failureKind: "ResolutionExhausted",
        failureReason:
          "Automatic conflict resolution ran 3 times against 'main' and merged the branches but left the code failing `npm run build`. This one needs a person.",
      });

      expect(await screen.findByText("mergePage.exhaustedTitle")).toBeInTheDocument();
      expect(screen.getByText("mergePage.exhaustedDesc")).toBeInTheDocument();
      expect(screen.getByText(/npm run build/)).toBeInTheDocument();
    });

    it("stops calling the AI combine recommended — it is the thing that just failed", async () => {
      show({ status: "exhausted", failureKind: "ResolutionExhausted" });

      expect(await screen.findByText("mergePage.alreadyTried")).toBeInTheDocument();
      expect(screen.queryByText("mergePage.recommended")).not.toBeInTheDocument();
      expect(screen.getByText("mergePage.combineRetryButton")).toBeInTheDocument();
      expect(screen.queryByText("mergePage.combineButton")).not.toBeInTheDocument();
    });

    it("is not a dead end — the deterministic side picks are still offered", async () => {
      show({ status: "exhausted", failureKind: "ResolutionExhausted" });

      // These do not need the resolver to succeed, so removing them would strand the user
      // on a page whose only advice is "give up".
      expect(await screen.findByText("mergePage.taskTitle")).toBeInTheDocument();
      expect(screen.getByText("mergePage.currentTitle")).toBeInTheDocument();
      expect(screen.getByText("mergePage.exhaustedNext")).toBeInTheDocument();
    });

    it("does not poll — nothing will change without the user", async () => {
      show({ status: "exhausted", failureKind: "ResolutionExhausted" });
      await screen.findByText("mergePage.exhaustedTitle");

      const callsAfterLoad = mockApi.tasks.getMergeConflicts.mock.calls.length;
      await new Promise((r) => setTimeout(r, 60));

      expect(mockApi.tasks.getMergeConflicts.mock.calls.length).toBe(callsAfterLoad);
    });
  });

  describe("blocked", () => {
    it("keeps its own screen rather than being folded into exhausted", async () => {
      show({
        status: "blocked",
        failureKind: "EnvironmentBlocked",
        failureReason: "Your repository has uncommitted changes.",
      });

      // Different problem, different advice: the user fixes their folder and it retries by
      // itself, so this one must not tell them automation has given up.
      expect(await screen.findByText("mergePage.blockedTitle")).toBeInTheDocument();
      expect(screen.getByText("mergePage.blockedAutoRetry")).toBeInTheDocument();
      expect(screen.queryByText("mergePage.exhaustedTitle")).not.toBeInTheDocument();
    });
  });

  describe("resolving", () => {
    it("names what is running and how long it has been going", async () => {
      // A bare spinner cannot tell "working" from "stuck". Both of these are what make the
      // difference, and both were missing when the page just span.
      show({
        status: "resolving",
        pendingStrategy: "combine",
        startedAt: new Date(Date.now() - 125_000).toISOString(),
      });

      await waitFor(() =>
        expect(screen.getByText("mergePage.resolvingTitle")).toBeInTheDocument()
      );
      expect(screen.getByText("mergePage.resolvingStrategyCombine")).toBeInTheDocument();
      // The value itself is asserted on formatElapsed below — the intl mock echoes keys
      // and drops interpolated values, so this can only prove the line is rendered.
      expect(screen.getByText("mergePage.resolvingElapsed")).toBeInTheDocument();
    });

    it("shows no clock when the server reports no start", async () => {
      // Older tasks, and the moment between arming and the worker picking it up.
      show({ status: "resolving", pendingStrategy: "combine", startedAt: null });

      await screen.findByText("mergePage.resolvingTitle");
      expect(screen.queryByText("mergePage.resolvingElapsed")).not.toBeInTheDocument();
    });

    it("offers to queue a follow-up rather than making the user wait to speak", async () => {
      show({ status: "resolving", pendingStrategy: "combine" });
      await screen.findByText("mergePage.resolvingTitle");

      const box = screen.getByPlaceholderText("mergePage.guidancePlaceholder");
      fireEvent.change(box, { target: { value: "keep the new colors" } });
      fireEvent.click(screen.getByText("mergePage.steerQueueButton"));

      await waitFor(() =>
        expect(mockApi.tasks.resolveMerge).toHaveBeenCalledWith(
          "task-1",
          expect.objectContaining({ strategy: "combine", guidance: "keep the new colors" }),
          expect.anything()
        )
      );
    });

    it("shows a queued follow-up, and lets it be called off", async () => {
      show({
        status: "resolving",
        pendingStrategy: "combine",
        queuedStrategy: "task",
        queuedGuidance: "keep the new colors",
      });

      expect(await screen.findByText(/mergePage.steerQueuedLabel/)).toBeInTheDocument();
      expect(screen.getByText(/keep the new colors/)).toBeInTheDocument();

      fireEvent.click(screen.getByText("mergePage.steerCancel"));
      await waitFor(() =>
        expect(mockApi.tasks.cancelQueuedMerge).toHaveBeenCalledWith("task-1", expect.anything())
      );
    });
  });

  it("keeps polling the chooser — a resolution can start without this page asking", async () => {
    // The regression that made the page unusable: a merge click arms the resolver
    // server-side, so a chooser that never re-reads offers buttons for work already running.
    show({ status: "awaitingChoice" });
    await screen.findByText("mergePage.combineButton");

    const afterLoad = mockApi.tasks.getMergeConflicts.mock.calls.length;
    await new Promise((r) => setTimeout(r, 5200));

    expect(mockApi.tasks.getMergeConflicts.mock.calls.length).toBeGreaterThan(afterLoad);
  }, 10_000);
});
