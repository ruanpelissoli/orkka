import { useTranslations } from "use-intl";
import { Button, GlassCard, Input, Textarea } from "@/components/ui";
import type { MilestonePlanResponse } from "@/lib/types";

interface MilestonePlanReviewProps {
  proposal: MilestonePlanResponse;
  onChange: (next: MilestonePlanResponse) => void;
  onAccept: () => void;
  onDiscard: () => void;
  busy: boolean;
  /**
   * Planning into a milestone that already exists. Its title and outcome are fixed — the
   * accept endpoint keeps the stored ones and ignores whatever the proposal carries — so the
   * fields are hidden rather than shown as edits that would silently be thrown away.
   */
  intoExistingMilestone?: boolean;
}

/**
 * The propose/accept review step. Extracted from the roadmap so the milestone detail page
 * reviews plans exactly the same way; two copies of "remove a task before it costs money"
 * would drift.
 */
export function MilestonePlanReview({
  proposal,
  onChange,
  onAccept,
  onDiscard,
  busy,
  intoExistingMilestone = false,
}: MilestonePlanReviewProps) {
  const t = useTranslations("dashboard");

  return (
    <GlassCard className="p-5">
      <div className="space-y-3">
        <div>
          <h3 className="font-display text-base font-bold text-white">
            {t("roadmapPage.proposalTitle")}
          </h3>
          <p className="mt-1 text-xs text-gray-400">{t("roadmapPage.proposalHint")}</p>
        </div>

        {!intoExistingMilestone && (
          <>
            <Input
              value={proposal.milestoneTitle}
              onChange={(e) => onChange({ ...proposal, milestoneTitle: e.target.value })}
              maxLength={200}
            />
            <Textarea
              value={proposal.outcome}
              onChange={(e) => onChange({ ...proposal, outcome: e.target.value })}
              maxLength={2000}
              rows={2}
            />
          </>
        )}

        <p className="text-xs text-gray-400">
          {t("roadmapPage.tasksProposed", { count: proposal.tasks.length })}
        </p>

        <ul className="space-y-2">
          {proposal.tasks.map((task, index) => (
            // Keyed by the proposal key, not the array index: removing a task must not make
            // React reuse the removed row's state for its neighbour.
            <li
              key={task.key}
              className="flex items-start justify-between gap-3 rounded-lg border border-white/[0.06] bg-white/[0.02] p-3"
            >
              <div className="min-w-0">
                <p className="text-sm text-gray-200">{task.title}</p>
                <p className="mt-0.5 text-label">
                  {task.type} · {task.priority}
                  {task.dependsOn && task.dependsOn.length > 0 && (
                    <> · {t("roadmapPage.dependsOnCount", { count: task.dependsOn.length })}</>
                  )}
                </p>
              </div>
              <button
                type="button"
                onClick={() =>
                  onChange({
                    ...proposal,
                    tasks: proposal.tasks.filter((_, i) => i !== index),
                  })
                }
                className="shrink-0 rounded-md px-2 py-1 text-xs text-gray-400 hover:bg-white/[0.06] hover:text-gray-300"
              >
                {t("roadmapPage.removeTask")}
              </button>
            </li>
          ))}
        </ul>

        <div className="flex gap-2">
          <Button size="sm" onClick={onAccept} disabled={busy || proposal.tasks.length === 0}>
            {t("roadmapPage.acceptPlan")}
          </Button>
          <Button size="sm" variant="ghost" onClick={onDiscard} disabled={busy}>
            {t("roadmapPage.discard")}
          </Button>
        </div>
      </div>
    </GlassCard>
  );
}
