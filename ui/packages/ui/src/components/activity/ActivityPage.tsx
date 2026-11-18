import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { Button, GlassCard, SegmentedControl, Select } from "@/components/ui";
import { useClientApi } from "@/hooks/use-client-api";
import { useBoardSignalR } from "@/hooks/use-board-signalr";
import { api } from "@/lib/api";
import { hasCostEstimate, formatUsdEstimate, formatTokenCount } from "@/lib/utils";
import { formatDuration } from "@/components/team/AgentOpsStats";
import type { ProjectAgent, ProjectExecutionsResponse } from "@/lib/types";
import {
  detectAttention,
  rollupByAgent,
  rowsInRange,
  summarize,
  topTasksBySpend,
} from "./activity-model";
import { ActivityDensityStrip } from "./ActivityDensityStrip";
import { AttentionList } from "./AttentionList";
import { OutcomeList } from "./OutcomeList";
import { SpendPanel } from "./SpendPanel";
import { ExecutionTimeline } from "./ExecutionTimeline";
import { RunDrawer } from "./RunDrawer";

type TimelineWindow = "24h" | "7d" | "30d";
type StatusFilter = "" | "Running" | "Completed" | "Failed" | "Cancelled";
type View = "digest" | "timeline";

interface ActivityPageProps {
  /** Route ref (slug or ULID) — what the API calls take. */
  projectId: string;
  /** Project ULID — the SignalR board group key. */
  projectUlid: string;
  /** Server-rendered first page for the default window (24h). */
  initial?: ProjectExecutionsResponse | null;
  /** Populates the agent filter. Absent or empty hides it. */
  agents?: ProjectAgent[];
}

const WINDOW_MS: Record<TimelineWindow, number> = {
  "24h": 24 * 60 * 60 * 1000,
  "7d": 7 * 24 * 60 * 60 * 1000,
  "30d": 30 * 24 * 60 * 60 * 1000,
};

/** Refetch cadence while at least one run is still open-ended. */
const LIVE_POLL_MS = 20_000;
/** Backstop when nothing is running (a queued run may start without any hub event we hear). */
const IDLE_POLL_MS = 60_000;

/**
 * The project Activity page: what the agent team did in a window, what it amounted to, and what it
 * cost — read as a shift report rather than as a chart of machine time.
 *
 * Three zones, worst-first: what wants a person, what moved, where the money went. The execution
 * timeline is still here behind a toggle, because it is genuinely the best way to see the *shape*
 * of a night's work; it is just a poor answer to "what happened", which is the question that
 * actually brings someone to this page.
 */
function ActivityPage({ projectId, projectUlid, initial, agents = [] }: ActivityPageProps) {
  const t = useTranslations("dashboard.activityPage");
  const withToken = useClientApi();

  const [view, setView] = useState<View>("digest");
  const [window, setWindow] = useState<TimelineWindow>("24h");
  const [status, setStatus] = useState<StatusFilter>("");
  const [agentId, setAgentId] = useState<string>("");
  const [data, setData] = useState<ProjectExecutionsResponse | null>(initial ?? null);
  const [isLoading, setIsLoading] = useState(!initial);
  const [error, setError] = useState<string | null>(null);
  const [openRun, setOpenRun] = useState<{ executionId: string; taskId: string } | null>(null);
  const [range, setRange] = useState<{ from: number; to: number } | null>(null);
  const requestRef = useRef(0);

  const refetch = useCallback(async () => {
    const requestId = ++requestRef.current;
    const to = new Date();
    const from = new Date(to.getTime() - WINDOW_MS[window]);
    try {
      const res = await withToken((token) =>
        api.executions.listForProject(
          projectId,
          {
            from: from.toISOString(),
            to: to.toISOString(),
            status: status || undefined,
            agentId: agentId || undefined,
            limit: 500,
          },
          token
        )
      );
      if (requestId !== requestRef.current) return;
      setData(res);
      setError(null);
    } catch {
      if (requestId !== requestRef.current) return;
      setError("load-failed");
    } finally {
      if (requestId === requestRef.current) setIsLoading(false);
    }
  }, [withToken, projectId, window, status, agentId]);

  // First fetch is skipped when the server rendered the default view; any filter change refetches.
  const skipFirstRef = useRef(Boolean(initial));
  useEffect(() => {
    if (skipFirstRef.current) {
      skipFirstRef.current = false;
      return;
    }
    setIsLoading(true);
    void refetch();
  }, [refetch]);

  const hasRunning = useMemo(
    () => (data?.executions ?? []).some((e) => e.status === "Running"),
    [data]
  );

  useEffect(() => {
    const id = setInterval(() => void refetch(), hasRunning ? LIVE_POLL_MS : IDLE_POLL_MS);
    return () => clearInterval(id);
  }, [hasRunning, refetch]);

  // Status and lane changes are the board-hub events that line up with a run starting or ending;
  // merge failures are the one outcome that appears with no execution behind it. Debounced because
  // a lane move fires more than one in quick succession.
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const scheduleRefetch = useCallback(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => void refetch(), 1500);
  }, [refetch]);
  useBoardSignalR({
    projectId: projectUlid,
    onStatusChanged: scheduleRefetch,
    onLaneChanged: scheduleRefetch,
    onTaskMergeFailed: scheduleRefetch,
  });

  const allRows = useMemo(() => data?.executions ?? [], [data]);
  const allTasks = useMemo(() => data?.tasks ?? [], [data]);
  const summary = data?.summary ?? null;
  // Before the first page arrives the window is anchored at mount time (render must stay pure).
  const [mountedAt] = useState(() => Date.now());
  const from = data ? new Date(data.from).getTime() : mountedAt - WINDOW_MS[window];
  const to = data ? new Date(data.to).getTime() : mountedAt;

  // A brushed range narrows the zones without refetching: the rows are already here, and the
  // point of dragging across the strip is to look closer at something you can already see.
  const rows = useMemo(
    () => (range ? rowsInRange(allRows, range.from, range.to) : allRows),
    [allRows, range]
  );
  const tasks = useMemo(() => {
    if (!range) return allTasks;
    const touched = new Set(rows.map((e) => e.taskId));
    return allTasks.filter((t) => touched.has(t.taskId));
  }, [allTasks, rows, range]);

  const rangeTotals = useMemo(() => summarize(rows), [rows]);
  const attention = useMemo(() => detectAttention(rows, tasks), [rows, tasks]);
  const agentRollups = useMemo(() => rollupByAgent(rows, range ? null : summary), [rows, summary, range]);
  const topTasks = useMemo(() => topTasksBySpend(rows), [rows]);
  const costless = Boolean(summary && summary.cost === 0 && summary.tokens > 0);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-2">
          <SegmentedControl
            options={[
              { value: "24h" as TimelineWindow, label: t("window24h") },
              { value: "7d" as TimelineWindow, label: t("window7d") },
              { value: "30d" as TimelineWindow, label: t("window30d") },
            ]}
            value={window}
            onChange={setWindow}
            aria-label={t("window7d")}
          />

          {agents.length > 0 && (
            <div className="w-44">
              <Select
                value={agentId}
                onChange={(e) => setAgentId(e.target.value)}
                className="py-1.5 text-xs"
                aria-label={t("allAgents")}
              >
                <option value="">{t("allAgents")}</option>
                {agents.map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.name}
                  </option>
                ))}
              </Select>
            </div>
          )}

          <div className="w-40">
            <Select
              value={status}
              onChange={(e) => setStatus(e.target.value as StatusFilter)}
              className="py-1.5 text-xs"
              aria-label={t("allStatuses")}
            >
              <option value="">{t("allStatuses")}</option>
              <option value="Running">{t("running")}</option>
              <option value="Completed">{t("completed")}</option>
              <option value="Failed">{t("failed")}</option>
              <option value="Cancelled">{t("cancelled")}</option>
            </Select>
          </div>
        </div>

        <SegmentedControl
          options={[
            { value: "digest" as View, label: t("viewDigest"), testId: "view-digest" },
            { value: "timeline" as View, label: t("viewTimeline"), testId: "view-timeline" },
          ]}
          value={view}
          onChange={setView}
          aria-label={t("viewDigest")}
        />
      </div>

      {allRows.length > 0 && (
        <ActivityDensityStrip
          rows={allRows}
          from={from}
          to={to}
          range={range}
          onRangeChange={setRange}
        />
      )}

      {/* Two strips, because they answer different questions. Without a brush the numbers come
          from the server and describe the whole window, truncation and all. With one, they can
          only describe the rows on screen — so tokens and wasted spend, which have no honest
          row-level equivalent, drop out rather than being quietly approximated. */}
      {range ? (
        <div
          className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-400"
          data-testid="activity-summary"
        >
          <span className="text-gray-200">{t("summaryRuns", { count: rangeTotals.runs })}</span>
          <span>{t("summaryTasks", { count: tasks.length })}</span>
          {rangeTotals.failed > 0 && (
            <span className="text-red-400">{t("summaryFailed", { count: rangeTotals.failed })}</span>
          )}
          <span>
            {t("summaryDuration", { duration: formatDuration(rangeTotals.durationSeconds) })}
          </span>
          {hasCostEstimate(rangeTotals.cost) && (
            <span>{t("summaryCost", { amount: formatUsdEstimate(rangeTotals.cost) })}</span>
          )}
        </div>
      ) : summary && summary.runs > 0 ? (
        <div
          className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-400"
          data-testid="activity-summary"
        >
          <span className="text-gray-200">{t("summaryRuns", { count: summary.runs })}</span>
          <span>{t("summaryTasks", { count: summary.tasksTouched })}</span>
          {summary.failed > 0 && (
            <span className="text-red-400">{t("summaryFailed", { count: summary.failed })}</span>
          )}
          <span>{t("summaryDuration", { duration: formatDuration(summary.durationSeconds) })}</span>
          {costless
            ? summary.tokens > 0 && (
                <span>{t("summaryTokens", { tokens: formatTokenCount(summary.tokens) })}</span>
              )
            : hasCostEstimate(summary.cost) && (
                <span>{t("summaryCost", { amount: formatUsdEstimate(summary.cost) })}</span>
              )}
          {!costless && hasCostEstimate(summary.wastedCost) && (
            <span className="text-red-400/90" title={t("wastedHint")}>
              {t("summaryWasted", { amount: formatUsdEstimate(summary.wastedCost) })}
            </span>
          )}
        </div>
      ) : null}

      {error ? (
        <GlassCard className="text-center">
          <p className="text-sm text-gray-400">{t("loadFailed")}</p>
          <Button size="sm" variant="ghost" className="mt-3" onClick={() => void refetch()}>
            {t("retry")}
          </Button>
        </GlassCard>
      ) : isLoading && rows.length === 0 ? (
        <GlassCard padding="none">
          <div className="space-y-2 p-4">
            {[0, 1, 2, 3].map((i) => (
              <div key={i} className="h-7 animate-pulse rounded bg-white/[0.04]" />
            ))}
          </div>
        </GlassCard>
      ) : rows.length === 0 ? (
        <GlassCard className="text-center">
          <p className="text-sm text-gray-300">{range ? t("emptyRange") : t("empty")}</p>
          <p className="mt-1 text-xs text-gray-400">
            {range ? t("emptyRangeHint") : t("emptyHint")}
          </p>
        </GlassCard>
      ) : view === "timeline" ? (
        <ExecutionTimeline
          projectId={projectId}
          rows={rows}
          from={from}
          to={to}
          onSelectRun={(r) => setOpenRun({ executionId: r.executionId, taskId: r.taskId })}
        />
      ) : (
        <div className="space-y-6">
          <Zone title={t("zoneAttention")} count={attention.length}>
            <AttentionList
              items={attention}
              projectId={projectId}
              onSelectRun={(executionId, taskId) => setOpenRun({ executionId, taskId })}
            />
          </Zone>
          <Zone title={t("zoneOutcomes")} count={tasks.length}>
            <OutcomeList tasks={tasks} rows={rows} projectId={projectId} />
          </Zone>
          <Zone title={costless ? t("zoneUsage") : t("zoneSpend")}>
            <SpendPanel
              agents={agentRollups}
              topTasks={topTasks}
              projectId={projectId}
              costless={costless}
            />
          </Zone>
        </div>
      )}

      {data?.truncated && (
        <p className="text-xs text-amber-400/90">{t("truncated", { count: rows.length })}</p>
      )}

      <RunDrawer
        target={openRun}
        onClose={() => {
          setOpenRun(null);
          // A retry from the drawer queues work the window should show; the poll would find
          // it eventually, but "nothing happened" is the wrong first impression.
          void refetch();
        }}
      />
    </div>
  );
}

function Zone({
  title,
  count,
  children,
}: {
  title: string;
  count?: number;
  children: React.ReactNode;
}) {
  return (
    <section className="space-y-2">
      <h3 className="text-label flex items-center gap-2">
        {title}
        {count !== undefined && count > 0 && (
          <span className="rounded-full bg-white/[0.06] px-1.5 py-0.5 text-xs font-normal text-gray-300">
            {count}
          </span>
        )}
      </h3>
      {children}
    </section>
  );
}

export { ActivityPage };
export type { ActivityPageProps };
