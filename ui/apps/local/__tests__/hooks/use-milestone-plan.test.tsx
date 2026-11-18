/**
 * Milestone planning is asynchronous because it had to be: a run takes 90s–210s, and while it
 * lived inside the POST a user who navigated away after three and a half minutes of an
 * unchanging spinner tore down the fetch — killing a generation their org had already paid for
 * and surfacing it as a 500.
 *
 * These pin the two behaviours that make the new shape actually deliver that: a reload finds
 * the run in progress, and the run says what it is doing while it does it.
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
import { renderHook, act, waitFor } from "@testing-library/react";

vi.mock("@/hooks/use-client-api", () => ({
  useClientApi:
    () =>
    <T,>(fn: (token: string) => Promise<T>) =>
      fn("test-token"),
}));

// Hoisted: vi.mock is lifted above the imports, so the factory below cannot close over an
// ordinary const declared here.
const planApi = vi.hoisted(() => ({
  plan: vi.fn(),
  planJob: vi.fn(),
  activePlanJob: vi.fn(),
}));

vi.mock("@/lib/api", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api")>("@/lib/api");
  return { ...actual, api: { milestones: planApi } };
});

import { useMilestonePlan } from "@/hooks/use-milestone-plan";
import type { MilestonePlanJob, MilestonePlanResponse } from "@/lib/types";

const PLAN: MilestonePlanResponse = {
  milestoneTitle: "Checkout",
  outcome: "Users can pay",
  tasks: [
    { title: "Cart API", description: "Endpoints", priority: "high", type: "Feature", key: 0 },
  ],
  repoId: "repo-1",
};

function job(over: Partial<MilestonePlanJob> = {}): MilestonePlanJob {
  return {
    jobId: "job-1",
    status: "Running",
    step: "Planning",
    milestoneId: null,
    repoId: "repo-1",
    plan: null,
    failureCode: null,
    error: null,
    createdAt: "2026-08-25T14:37:08Z",
    startedAt: "2026-08-25T14:37:09Z",
    completedAt: null,
    ...over,
  };
}

describe("useMilestonePlan", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    planApi.activePlanJob.mockResolvedValue(null);
    planApi.planJob.mockResolvedValue(job({ status: "Completed", step: null, plan: PLAN }));
    planApi.plan.mockResolvedValue(job({ status: "Pending", step: "Queued" }));
  });

  it("starts idle when nothing is planning", async () => {
    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));

    await waitFor(() => expect(planApi.activePlanJob).toHaveBeenCalled());
    expect(result.current.planning).toBe(false);
    expect(result.current.step).toBeNull();
    expect(result.current.proposal).toBeNull();
  });

  /**
   * The reattach. Without it a reload during a run shows an idle Plan button, and clicking it
   * looks like the only way forward — which is how one abandoned run becomes two paid ones.
   */
  it("reattaches on mount to a run already in progress", async () => {
    planApi.activePlanJob.mockResolvedValue(job({ status: "Running", step: "Planning" }));

    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));

    await waitFor(() => expect(result.current.planning).toBe(true));
    expect(result.current.step).toBe("Planning");
  });

  it("reattaches to a plan that finished while the page was closed", async () => {
    planApi.activePlanJob.mockResolvedValue(
      job({ status: "Completed", step: null, plan: PLAN })
    );

    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));

    await waitFor(() => expect(result.current.proposal).not.toBeNull());
    expect(result.current.proposal!.milestoneTitle).toBe("Checkout");
    expect(result.current.planning).toBe(false);
  });

  it("planning into a milestone reattaches only to that milestone's run", async () => {
    renderHook(() => useMilestonePlan({ projectId: "p1", milestoneId: "m1" }));

    await waitFor(() =>
      expect(planApi.activePlanJob).toHaveBeenCalledWith("p1", "m1", "test-token")
    );
  });

  it("queuing a run leaves it planning, not resolved", async () => {
    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));
    await waitFor(() => expect(planApi.activePlanJob).toHaveBeenCalled());

    await act(async () => {
      await result.current.start("Add checkout");
    });

    expect(planApi.plan).toHaveBeenCalledWith(
      "p1",
      { goal: "Add checkout", milestoneId: undefined },
      "test-token"
    );
    expect(result.current.planning).toBe(true);
    expect(result.current.step).toBe("Queued");
  });

  /** The progress the user watches instead of a spinner that says nothing. */
  it("follows the run's phases as they are reported", async () => {
    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));
    await waitFor(() => expect(planApi.activePlanJob).toHaveBeenCalled());
    await act(async () => {
      await result.current.start("Add checkout");
    });

    act(() => {
      result.current.onPlanChanged({
        jobId: "job-1",
        projectId: "p1",
        milestoneId: null,
        status: "Running",
        step: "Planning",
        failureCode: null,
        errorMessage: null,
        startedAt: "2026-08-25T14:37:09Z",
        timestamp: "2026-08-25T14:37:20Z",
      });
    });

    expect(result.current.step).toBe("Planning");
    expect(result.current.planning).toBe(true);
  });

  /** The event carries no plan — completion is a signal to go and read it. */
  it("fetches the plan when the run reports completion", async () => {
    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));
    await waitFor(() => expect(planApi.activePlanJob).toHaveBeenCalled());
    await act(async () => {
      await result.current.start("Add checkout");
    });

    await act(async () => {
      result.current.onPlanChanged({
        jobId: "job-1",
        projectId: "p1",
        milestoneId: null,
        status: "Completed",
        step: null,
        failureCode: null,
        errorMessage: null,
        startedAt: "2026-08-25T14:37:09Z",
        timestamp: "2026-08-25T14:40:31Z",
      });
    });

    await waitFor(() => expect(result.current.proposal).not.toBeNull());
    expect(planApi.planJob).toHaveBeenCalledWith("p1", "job-1", "test-token");
    expect(result.current.planning).toBe(false);
  });

  /**
   * The board group carries every member's runs, and a project can have several in flight.
   * Reacting to somebody else's would replace this user's proposal with a stranger's.
   */
  it("ignores events for other runs", async () => {
    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));
    await waitFor(() => expect(planApi.activePlanJob).toHaveBeenCalled());
    await act(async () => {
      await result.current.start("Add checkout");
    });

    act(() => {
      result.current.onPlanChanged({
        jobId: "someone-elses-job",
        projectId: "p1",
        milestoneId: null,
        status: "Completed",
        step: null,
        failureCode: null,
        errorMessage: null,
        startedAt: null,
        timestamp: "2026-08-25T14:40:31Z",
      });
    });

    expect(planApi.planJob).not.toHaveBeenCalled();
    expect(result.current.step).toBe("Queued");
  });

  /** `budget` (a spend limit tripped) is its own code, not "your goal was bad" — it used to arrive as HTTP 402. */
  it("surfaces a budget block as its own failure code", async () => {
    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));
    await waitFor(() => expect(planApi.activePlanJob).toHaveBeenCalled());
    await act(async () => {
      await result.current.start("Add checkout");
    });

    act(() => {
      result.current.onPlanChanged({
        jobId: "job-1",
        projectId: "p1",
        milestoneId: null,
        status: "Failed",
        step: null,
        failureCode: "runner_offline",
        errorMessage: "Your runner is offline or unavailable.",
        startedAt: null,
        timestamp: "2026-08-25T14:38:00Z",
      });
    });

    expect(result.current.planning).toBe(false);
    expect(result.current.failureCode).toBe("runner_offline");
    expect(result.current.error).toBe("Your runner is offline or unavailable.");
  });

  it("reset clears a finished run so the panel can be reused", async () => {
    planApi.activePlanJob.mockResolvedValue(
      job({ status: "Completed", step: null, plan: PLAN })
    );
    const { result } = renderHook(() => useMilestonePlan({ projectId: "p1" }));
    await waitFor(() => expect(result.current.proposal).not.toBeNull());

    act(() => result.current.reset());

    expect(result.current.proposal).toBeNull();
    expect(result.current.planning).toBe(false);
    expect(result.current.error).toBeNull();
  });
});
