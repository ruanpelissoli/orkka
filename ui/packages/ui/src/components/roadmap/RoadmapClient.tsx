import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Button, GlassCard, Input, Textarea } from "@/components/ui";
import { MilestoneForecastSummary } from "@/components/forecast";
import { MilestonePlanReview } from "./MilestonePlanReview";
import { MilestonePlanProgress } from "./MilestonePlanProgress";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { useClientApi } from "@/hooks/use-client-api";
import { useBoardSignalR, type MilestoneChangedEvent } from "@/hooks/use-board-signalr";
import { useMilestonePlan } from "@/hooks/use-milestone-plan";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import type {
  Milestone,
  MilestoneCounts,
  MilestoneStatusKey,
  MilestoneForecast,
} from "@/lib/types";

interface RoadmapClientProps {
  /** Route ref used in URLs — the project slug (or a legacy ULID) */
  projectId: string;
  /** Project ULID — SignalR board groups are keyed by it */
  projectUlid: string;
  initialMilestones: Milestone[];
  initialBacklog: MilestoneCounts;
}

const STATUS_TONE: Record<MilestoneStatusKey, string> = {
  Planned: "text-gray-400 bg-white/[0.04] border-white/[0.06]",
  Active: "text-primary-300 bg-primary-500/10 border-primary-500/20",
  Completed: "text-emerald-300 bg-emerald-500/10 border-emerald-500/20",
  Cancelled: "text-gray-400 bg-white/[0.02] border-white/[0.04]",
};

export function RoadmapClient({
  projectId,
  projectUlid,
  initialMilestones,
  initialBacklog,
}: RoadmapClientProps) {
  const t = useTranslations("dashboard");
  const withToken = useClientApi();

  const [milestones, setMilestones] = useState<Milestone[]>(initialMilestones);
  // Fetched after render: the list endpoint is one count query, while each forecast walks
  // every open task under a milestone. Keeping them separate keeps the page fast.
  const [forecasts, setForecasts] = useState<Record<string, MilestoneForecast | null>>({});
  const [backlog, setBacklog] = useState<MilestoneCounts>(initialBacklog);
  const [creating, setCreating] = useState(false);
  const [planOpen, setPlanOpen] = useState(false);
  const [accepting, setAccepting] = useState(false);
  const [goal, setGoal] = useState("");
  const [editingId, setEditingId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // The run lives on the server, not in this component: it survives a reload, a second tab, and
  // the user closing the laptop. All this owns is whether the panel is open.
  const {
    proposal,
    setProposal,
    planning,
    step,
    elapsedSeconds,
    failureCode,
    error: planError,
    start,
    reset: resetPlan,
    onPlanChanged,
  } = useMilestonePlan({ projectId });

  // Counts arrive fully computed — replace, never merge. Deletion drops the row.
  const handleMilestoneChanged = useCallback((event: MilestoneChangedEvent) => {
    setMilestones((current) => {
      if (event.deleted) return current.filter((m) => m.id !== event.milestoneId);
      return current.map((m) =>
        m.id === event.milestoneId
          ? {
              ...m,
              status: (event.status as MilestoneStatusKey) ?? m.status,
              counts: {
                total: event.total,
                done: event.done,
                inFlight: event.inFlight,
                needsAttention: event.needsAttention,
                notStarted: event.notStarted,
              },
            }
          : m
      );
    });
  }, []);

  // The roadmap joins the same board group the kanban uses — one group per project.
  useBoardSignalR({
    projectId: projectUlid,
    onMilestoneChanged: handleMilestoneChanged,
    onMilestonePlanChanged: onPlanChanged,
  });

  // A run reattached on mount (or started in another tab) opens the panel by itself —
  // otherwise the page would be quietly planning behind a closed drawer. Adjusted during render
  // whenever either changes (and once on mount, for a run reattached on load).
  const [seenPlan, setSeenPlan] = useState<{ planning: typeof planning; proposal: typeof proposal } | null>(null);
  if (!seenPlan || seenPlan.planning !== planning || seenPlan.proposal !== proposal) {
    setSeenPlan({ planning, proposal });
    if (planning || proposal) setPlanOpen(true);
  }

  useEffect(() => {
    let cancelled = false;
    (async () => {
      for (const milestone of milestones) {
        try {
          const forecast = await withToken((token) =>
            api.milestones.forecast(projectId, milestone.id, token)
          );
          if (cancelled) return;
          setForecasts((current) => ({ ...current, [milestone.id]: forecast }));
        } catch {
          // A missing forecast renders as nothing at all, which is the correct empty state.
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [milestones, withToken, projectId]);

  const reload = useCallback(async () => {
    const data = await withToken((token) => api.milestones.list(projectId, token));
    setMilestones(data.milestones);
    setBacklog(data.backlog);
  }, [withToken, projectId]);

  const plan = useCallback(() => {
    if (!goal.trim()) return;
    void start(goal);
  }, [goal, start]);

  // An offline runner is "start your runner", not a failure to plan: planning runs there, on the
  // developer's own AI subscription — so the UI says how to fix it instead of telling the user
  // their goal was bad.
  const planErrorMessage =
    planError === null
      ? null
      : failureCode === "runner_offline"
        ? t("roadmapPage.planRunnerOffline")
        : failureCode === "unusable"
          ? t("roadmapPage.planRetry")
          : t("roadmapPage.planFailed");

  const acceptPlan = useCallback(async () => {
    if (!proposal || proposal.tasks.length === 0) return;
    setAccepting(true);
    try {
      await withToken((token) =>
        api.milestones.accept(
          projectId,
          {
            milestoneTitle: proposal.milestoneTitle,
            outcome: proposal.outcome,
            tasks: proposal.tasks,
            // Without this every planned task lands with no repo and SubmitTask refuses it.
            repoId: proposal.repoId ?? undefined,
            // Without this the halves of a cross-repo task are created as unrelated tasks:
            // no shared contract, no merge gate.
            contracts: proposal.contracts ?? undefined,
          },
          token
        )
      );
      resetPlan();
      setGoal("");
      setPlanOpen(false);
      await reload();
    } finally {
      setAccepting(false);
    }
  }, [proposal, withToken, projectId, reload, resetPlan]);

  const create = useCallback(
    async (title: string, outcome: string) => {
      setBusy(true);
      try {
        await withToken((token) =>
          api.milestones.create(projectId, { title, outcome: outcome || undefined }, token)
        );
        setCreating(false);
        await reload();
      } finally {
        setBusy(false);
      }
    },
    [withToken, projectId, reload]
  );

  const save = useCallback(
    async (id: string, title: string, outcome: string) => {
      setBusy(true);
      try {
        await withToken((token) =>
          api.milestones.update(projectId, id, { title, outcome }, token)
        );
        setEditingId(null);
        await reload();
      } finally {
        setBusy(false);
      }
    },
    [withToken, projectId, reload]
  );

  const remove = useCallback(
    async (id: string) => {
      if (!window.confirm(t("roadmapPage.deleteConfirm"))) return;
      setBusy(true);
      try {
        await withToken((token) => api.milestones.delete(projectId, id, token));
        await reload();
      } finally {
        setBusy(false);
      }
    },
    [withToken, projectId, reload, t]
  );

  // Position is the only ordering signal — swap with the neighbour and persist both.
  const move = useCallback(
    async (index: number, direction: -1 | 1) => {
      const target = index + direction;
      if (target < 0 || target >= milestones.length) return;

      const a = milestones[index];
      const b = milestones[target];
      setBusy(true);
      try {
        await withToken(async (token) => {
          await api.milestones.update(projectId, a.id, { position: b.position }, token);
          await api.milestones.update(projectId, b.id, { position: a.position }, token);
        });
        await reload();
      } finally {
        setBusy(false);
      }
    },
    [milestones, withToken, projectId, reload]
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap justify-end gap-2">
        <RequirePermission permission="TasksCreate">
          <Button
            size="sm"
            variant="ghost"
            onClick={() => setPlanOpen(true)}
            disabled={planOpen || busy}
          >
            {t("roadmapPage.planWithAi")}
          </Button>
          <Button size="sm" onClick={() => setCreating(true)} disabled={creating || busy}>
            {t("roadmapPage.newMilestone")}
          </Button>
        </RequirePermission>
      </div>

      {planOpen && (
        <GlassCard className="p-5">
          {proposal === null ? (
            <div className="space-y-3">
              <label className="block text-xs text-gray-400">
                {t("roadmapPage.goalLabel")}
              </label>
              <Textarea
                value={goal}
                onChange={(e) => setGoal(e.target.value)}
                placeholder={t("roadmapPage.goalPlaceholder")}
                maxLength={2000}
                rows={3}
                disabled={planning}
              />
              {/* The whole point of the queue: a named phase and a running clock, in place of a
                  spinner that told the user nothing for three and a half minutes. */}
              {planning && step && (
                <MilestonePlanProgress step={step} elapsedSeconds={elapsedSeconds} />
              )}
              {planErrorMessage && (
                <p className="text-xs text-amber-400">{planErrorMessage}</p>
              )}
              <div className="flex gap-2">
                <Button size="sm" onClick={plan} disabled={planning || !goal.trim()}>
                  {planning ? t("roadmapPage.planning") : t("roadmapPage.planWithAi")}
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => {
                    setPlanOpen(false);
                    // Closing the panel only stops watching. The run is on the server and
                    // finishes either way — reopening, or reloading, reattaches to it.
                    if (!planning) resetPlan();
                  }}
                >
                  {t("roadmapPage.cancel")}
                </Button>
              </div>
            </div>
          ) : (
            <MilestonePlanReview
              proposal={proposal}
              onChange={setProposal}
              onAccept={acceptPlan}
              onDiscard={resetPlan}
              busy={accepting}
            />
          )}
        </GlassCard>
      )}

      {creating && (
        <MilestoneForm
          onCancel={() => setCreating(false)}
          onSubmit={create}
          submitLabel={t("roadmapPage.create")}
          busy={busy}
        />
      )}

      {milestones.length === 0 && !creating && (
        <GlassCard className="p-6">
          <p className="text-sm text-gray-400">{t("roadmapPage.empty")}</p>
        </GlassCard>
      )}

      {milestones.map((milestone, index) =>
        editingId === milestone.id ? (
          <MilestoneForm
            key={milestone.id}
            initialTitle={milestone.title}
            initialOutcome={milestone.outcome ?? ""}
            onCancel={() => setEditingId(null)}
            onSubmit={(title, outcome) => save(milestone.id, title, outcome)}
            submitLabel={t("roadmapPage.save")}
            busy={busy}
          />
        ) : (
          <GlassCard key={milestone.id} className="p-5">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <h3 className="font-display text-base font-bold text-white">
                    {milestone.title}
                  </h3>
                  <span
                    className={cn(
                      "rounded-full border px-2 py-0.5 text-label",
                      STATUS_TONE[milestone.status]
                    )}
                  >
                    {t(`roadmapPage.status.${milestone.status}`)}
                  </span>
                  {/* Only rendered when the server resolved one — on a single-repo project
                      every card would carry the same label, which says nothing. */}
                  {milestone.repoName && (
                    <span className="rounded-full border border-white/[0.06] bg-white/[0.04] px-2 py-0.5 font-mono text-xs text-gray-400">
                      {milestone.repoName}
                    </span>
                  )}
                </div>
                <p
                  className={cn(
                    "mt-1 text-sm",
                    milestone.outcome ? "text-gray-400" : "italic text-gray-400"
                  )}
                >
                  {milestone.outcome ?? t("roadmapPage.noOutcome")}
                </p>
              </div>

              <div className="flex shrink-0 items-center gap-1">
                <RequirePermission permission="TasksEdit">
                  <IconButton
                    label={t("roadmapPage.moveUp")}
                    disabled={index === 0 || busy}
                    onClick={() => move(index, -1)}
                  >
                    ↑
                  </IconButton>
                  <IconButton
                    label={t("roadmapPage.moveDown")}
                    disabled={index === milestones.length - 1 || busy}
                    onClick={() => move(index, 1)}
                  >
                    ↓
                  </IconButton>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => setEditingId(milestone.id)}
                    disabled={busy}
                  >
                    {t("roadmapPage.edit")}
                  </Button>
                </RequirePermission>
                <RequirePermission permission="TasksDelete">
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => remove(milestone.id)}
                    disabled={busy}
                  >
                    {t("roadmapPage.delete")}
                  </Button>
                </RequirePermission>
              </div>
            </div>

            <ProgressRow counts={milestone.counts} />

            <MilestoneForecastSummary
              forecast={forecasts[milestone.id] ?? null}
              className="mt-2"
            />

            {/* Into the milestone, not the board: an empty milestone is where its tasks get
                planned, and the board has nothing to show for one. */}
            <Link
              href={`/p/${projectId}/roadmap/${milestone.id}`}
              className="mt-3 inline-block text-xs text-primary-400 hover:text-primary-300"
            >
              {milestone.counts.total === 0
                ? t("milestonePage.generateTasks")
                : t("milestonePage.tasksHeading", { count: milestone.counts.total })}{" "}
              →
            </Link>
          </GlassCard>
        )
      )}

      {/* The backlog is not a milestone and has no row of its own server-side — it is
          whatever hasn't been planned into one yet. */}
      <GlassCard className="p-5">
        <h3 className="font-display text-base font-bold text-gray-300">
          {t("roadmapPage.backlog")}
        </h3>
        <p className="mt-1 text-sm text-gray-400">{t("roadmapPage.backlogHint")}</p>
        <ProgressRow counts={backlog} />
      </GlassCard>
    </div>
  );
}

function ProgressRow({ counts }: { counts: MilestoneCounts }) {
  const t = useTranslations("dashboard");

  if (counts.total === 0) {
    return <p className="mt-4 text-xs text-gray-400">{t("roadmapPage.noTasks")}</p>;
  }

  const donePct = Math.round((counts.done / counts.total) * 100);
  const attentionPct = Math.round((counts.needsAttention / counts.total) * 100);
  const inFlightPct = Math.round((counts.inFlight / counts.total) * 100);

  return (
    <div className="mt-4">
      <div className="flex h-1.5 overflow-hidden rounded-full bg-white/[0.06]">
        <div className="bg-emerald-500/70" style={{ width: `${donePct}%` }} />
        <div className="bg-primary-500/70" style={{ width: `${inFlightPct}%` }} />
        <div className="bg-amber-500/70" style={{ width: `${attentionPct}%` }} />
      </div>
      <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-400">
        <span className="text-gray-400">
          {t("roadmapPage.progress", { done: counts.done, total: counts.total })}
        </span>
        {counts.inFlight > 0 && <span>{t("roadmapPage.inFlight", { count: counts.inFlight })}</span>}
        {counts.notStarted > 0 && (
          <span>{t("roadmapPage.notStarted", { count: counts.notStarted })}</span>
        )}
        {counts.needsAttention > 0 && (
          <span className="text-amber-400">
            {t("roadmapPage.needsAttention", { count: counts.needsAttention })}
          </span>
        )}
      </div>
    </div>
  );
}

function IconButton({
  label,
  disabled,
  onClick,
  children,
}: {
  label: string;
  disabled?: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={onClick}
      className={cn(
        "rounded-md px-2 py-1 text-xs text-gray-400",
        "hover:bg-white/[0.06] hover:text-gray-200",
        "disabled:cursor-not-allowed disabled:opacity-30"
      )}
    >
      {children}
    </button>
  );
}

function MilestoneForm({
  initialTitle = "",
  initialOutcome = "",
  onCancel,
  onSubmit,
  submitLabel,
  busy,
}: {
  initialTitle?: string;
  initialOutcome?: string;
  onCancel: () => void;
  onSubmit: (title: string, outcome: string) => void;
  submitLabel: string;
  busy: boolean;
}) {
  const t = useTranslations("dashboard");
  const [title, setTitle] = useState(initialTitle);
  const [outcome, setOutcome] = useState(initialOutcome);

  return (
    <GlassCard className="p-5">
      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (title.trim()) onSubmit(title.trim(), outcome.trim());
        }}
        className="space-y-3"
      >
        <div>
          <label className="mb-1 block text-xs text-gray-400">
            {t("roadmapPage.titleLabel")}
          </label>
          <Input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} required />
        </div>
        <div>
          <label className="mb-1 block text-xs text-gray-400">
            {t("roadmapPage.outcomeLabel")}
          </label>
          <Textarea
            value={outcome}
            onChange={(e) => setOutcome(e.target.value)}
            placeholder={t("roadmapPage.outcomePlaceholder")}
            maxLength={2000}
            rows={2}
          />
        </div>
        <div className="flex gap-2">
          <Button type="submit" size="sm" disabled={busy || !title.trim()}>
            {submitLabel}
          </Button>
          <Button type="button" size="sm" variant="ghost" onClick={onCancel} disabled={busy}>
            {t("roadmapPage.cancel")}
          </Button>
        </div>
      </form>
    </GlassCard>
  );
}
