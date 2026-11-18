import { useMemo } from "react";
import { TaskCreateForm } from "@/components/task/TaskCreateForm";
import type { TaskBillingContext, TaskModelOption } from "@/components/task/TaskCreateForm";
import { useSelectableModels } from "@/hooks/use-selectable-models";
import type { AiModel, Milestone, ProjectAgent, ProjectRepo } from "@/lib/types";

/**
 * The task form sources its model picker from the runner-aware catalog: which
 * providers the user's runner has signed in (live, or last-known while it is offline) decide,
 * per model, whether a task can run now or waits for a sign-in. Those hooks live here so the
 * page only loads data.
 *
 * While the runner status is still loading the plain catalog renders, with no readiness
 * labels; the annotated list replaces it once it is known.
 */
export function TaskCreateFormWithModels({
  projectId,
  repos,
  agents,
  catalog,
  returnTo,
  milestones,
  defaultMilestoneId,
}: {
  projectId: string;
  repos: ProjectRepo[];
  agents: ProjectAgent[];
  /** The full chat catalog — runner-only rows included; the hook decides what is listed. */
  catalog: AiModel[];
  returnTo: string;
  milestones?: Milestone[];
  defaultMilestoneId?: string;
}) {
  const { models: annotated, coverage } = useSelectableModels(catalog);

  const models = useMemo<TaskModelOption[]>(
    () =>
      annotated
        ? annotated.map((m) => ({
            id: m.id,
            name: m.name,
            provider: m.provider,
            sortOrder: m.sortOrder,
            connected: m.connected,
            billing: m.billing,
            reason: m.reason,
          }))
        : catalog
            .filter((m) => m.kind === "Chat" && m.isEnabled)
            .map((m) => ({ id: m.id, name: m.name, provider: m.provider, sortOrder: m.sortOrder })),
    [annotated, catalog]
  );

  const billingContext = useMemo<TaskBillingContext | undefined>(
    () =>
      annotated ? { coverage, annotated } : undefined,
    [annotated, coverage]
  );

  return (
    <TaskCreateForm
      projectId={projectId}
      repos={repos}
      agents={agents}
      models={models}
      returnTo={returnTo}
      milestones={milestones}
      defaultMilestoneId={defaultMilestoneId}
      billingContext={billingContext}
    />
  );
}
