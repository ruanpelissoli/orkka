import {
  lazy,
  Suspense,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ComponentProps,
} from "react";
import { useTranslations } from "use-intl";
import {
  Button,
  Modal,
  Input,
  Toggle,
  FloatingDockSlot,
  FLOATING_DOCK_ORDER,
} from "@/components/ui";
import { cn } from "@/lib/utils";
import { useProject } from "@/hooks/use-project";
import { useRunnerStatus } from "@/hooks/use-runner-status";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import type { ProjectRunConfig, RunTarget } from "@/lib/types";
import { useAppRun, type AppRunSnapshot } from "./useAppRun";

// xterm.js touches document/window at import time: its own chunk, loaded on first use.
const LazyRunLogView = lazy(() =>
  import("./RunLogView").then((m) => ({ default: m.RunLogView }))
);

function RunLogView(props: ComponentProps<typeof LazyRunLogView>) {
  return (
    <Suspense fallback={null}>
      <LazyRunLogView {...props} />
    </Suspense>
  );
}

const KIND_STYLES: Record<string, string> = {
  web: "bg-cyan-500/15 text-cyan-300",
  api: "bg-violet-500/15 text-violet-300",
  mobile: "bg-emerald-500/15 text-emerald-300",
  desktop: "bg-amber-500/15 text-amber-300",
  worker: "bg-amber-500/15 text-amber-300",
  database: "bg-red-500/15 text-red-300",
  other: "bg-white/[0.08] text-gray-300",
};

/**
 * The Run button: starts this project's apps on the user's own machine through their
 * connected runner, over REST + tasks-hub events.
 *
 * An org member may have no runner at all, so the dock renders nothing unless the CALLER's runner is online and app-run
 * capable — a cloud-only user never sees a play button that cannot work.
 */
export function RunDock() {
  const t = useTranslations("dashboard.run");
  const { currentProject, refreshProject } = useProject();
  const runnerStatus = useRunnerStatus();
  const withToken = useClientApi();

  const [open, setOpen] = useState(false);
  const [boundProjectId, setBoundProjectId] = useState<string | null>(null);
  const [config, setConfig] = useState<ProjectRunConfig | null>(null);
  const [loading, setLoading] = useState(false);
  const [detecting, setDetecting] = useState(false);
  const [starting, setStarting] = useState(false);
  const [savingKeepRunning, setSavingKeepRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string[] | null>(null);
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const [editing, setEditing] = useState<RunTarget | null>(null);
  // Which project auto-start has already been attempted for — once per project per tab,
  // so a user who stopped the app is not fought by a dock that keeps restarting it.
  const autoStartedFor = useRef<string | null>(null);

  // Connected as soon as the user is in a project, not on first open: the bubble's badge has
  // to know that something is already running before anyone clicks it.
  const projectId = currentProject?.id ?? boundProjectId;
  const { snapshots, supported, attached, start, stop, subscribeOutput, sendInput, sendResize } =
    useAppRun(projectId);

  // Rebind when the user enters a different project. Adjusted during render (React's
  // documented shape for derived state) so the panel never paints one project's runs while
  // labelled with another's.
  if (currentProject && currentProject.id !== boundProjectId) {
    setBoundProjectId(currentProject.id);
    setConfig(null);
    setSelected(null);
    setActiveRunId(null);
    setError(null);
  }

  // Load lazily: the run config costs a query, and most sessions never open this panel.
  // The project whose config the open panel still needs; entering that state shows the
  // loading state (adjusted during render), and the effect below fetches it.
  const configToLoad = open && config === null ? boundProjectId : null;
  const [prevConfigToLoad, setPrevConfigToLoad] = useState<string | null>(null);
  if (configToLoad !== prevConfigToLoad) {
    setPrevConfigToLoad(configToLoad);
    if (configToLoad) {
      setLoading(true);
      setError(null);
    }
  }

  useEffect(() => {
    if (!configToLoad) return;
    withToken((token) => api.runConfig.get(configToLoad, token))
      .then((next) => setConfig(next))
      .catch((e: unknown) => setError(extractErrorMessage(e, t("loadFailed"))))
      .finally(() => setLoading(false));
  }, [configToLoad, withToken, t]);

  const targets = useMemo(() => config?.targets ?? [], [config]);

  // Default selection follows the detector's own judgement — the web app and its API, not
  // the mobile app that needs a simulator.
  const effectiveSelection = useMemo(
    () => selected ?? targets.filter((target) => target.autoSelect).map((target) => target.key),
    [selected, targets]
  );

  const running = snapshots.filter((s) => s.status === "running");
  const portBlocked = snapshots.some((s) => s.portConflict);
  const anyFailed = snapshots.some((s) => s.status === "failed");

  // The "Live app" link: the running target a person opens — the web app first. Until its
  // port answers it reads "starting…", so nobody lands on a dead tab.
  const liveTargets = running.filter((s) => s.previewUrl);
  const primaryLive = liveTargets.find((s) => s.kind === "web") ?? liveTargets[0] ?? null;

  const activeSnapshot =
    snapshots.find((s) => s.runId === activeRunId) ?? snapshots[snapshots.length - 1] ?? null;

  const keepRunning = currentProject?.settings?.autoRunOnOpen === true;
  // The attach response says whether the caller's runner can run at all.
  const runnerReady = supported;

  const toggle = useCallback(
    (key: string) => {
      setSelected((prev) => {
        const base = prev ?? targets.filter((x) => x.autoSelect).map((x) => x.key);
        return base.includes(key) ? base.filter((k) => k !== key) : [...base, key];
      });
    },
    [targets]
  );

  const handleDetect = useCallback(
    async (force: boolean) => {
      if (!boundProjectId) return;
      setDetecting(true);
      setError(null);
      try {
        const next = await withToken((token) =>
          api.runConfig.detect(boundProjectId, token, force)
        );
        setConfig(next);
        setSelected(null);
      } catch (e) {
        setError(extractErrorMessage(e, t("detectFailed")));
      } finally {
        setDetecting(false);
      }
    },
    [boundProjectId, withToken, t]
  );

  const handleStart = useCallback(
    async (forceFreePorts: boolean) => {
      if (effectiveSelection.length === 0) return;
      setStarting(true);
      setError(null);
      try {
        const started = await start(effectiveSelection, forceFreePorts);
        const firstLive = started.find((s) => s.status === "running");
        if (firstLive) setActiveRunId(firstLive.runId);
        else if (started.length > 0) setActiveRunId(started[0].runId);
      } catch (e) {
        setError(extractErrorMessage(e, t("startFailed")));
      } finally {
        setStarting(false);
      }
    },
    [effectiveSelection, start, t]
  );

  // Keep running: the project asked to be up whenever it is open. Once the initial attach has
  // told us nothing is running, start the auto-select targets — same call as the plain Run
  // press (an empty selection means "everything auto-select", resolved server-side from the
  // stored config, so no config load is needed here). Attempted once per project per tab.
  useEffect(() => {
    if (!keepRunning || !attached || !boundProjectId || !runnerReady) return;
    if (autoStartedFor.current === boundProjectId) return;
    autoStartedFor.current = boundProjectId;
    if (running.length > 0) return;
    void (async () => {
      setStarting(true);
      setError(null);
      try {
        const started = await start([], false);
        const firstLive = started.find((s) => s.status === "running");
        if (firstLive) setActiveRunId(firstLive.runId);
        else if (started.length > 0) setActiveRunId(started[0].runId);
      } catch (e) {
        setError(extractErrorMessage(e, t("startFailed")));
      } finally {
        setStarting(false);
      }
    })();
  }, [keepRunning, attached, boundProjectId, runnerReady, running.length, start, t]);

  const handleKeepRunning = useCallback(
    async (value: boolean) => {
      if (!currentProject) return;
      setSavingKeepRunning(true);
      setError(null);
      try {
        // The settings PATCH replaces the whole settings JSON — spread the current ones.
        await withToken((token) =>
          api.projects.update(
            currentProject.id,
            { settings: { ...currentProject.settings, autoRunOnOpen: value } },
            token
          )
        );
        await refreshProject(currentProject.id);
      } catch (e) {
        setError(extractErrorMessage(e, t("saveFailed")));
      } finally {
        setSavingKeepRunning(false);
      }
    },
    [currentProject, refreshProject, withToken, t]
  );

  const handleSaveTarget = useCallback(
    async (updated: RunTarget) => {
      if (!boundProjectId || !config) return;
      const next = config.targets.map((target) =>
        target.key === updated.key ? updated : target
      );
      setConfig(
        await withToken((token) =>
          api.runConfig.save(boundProjectId, { targets: next, notes: config.notes }, token)
        )
      );
      setEditing(null);
    },
    [boundProjectId, config, withToken]
  );

  if (!boundProjectId && !currentProject) return null;

  const inProject = currentProject !== null;
  // No dock without the caller's own runner online. Absent capability list =
  // older status payload; assume capable rather than hiding the feature.
  if (runnerStatus?.online !== true) return null;
  if (
    Array.isArray(runnerStatus.capabilities) &&
    !runnerStatus.capabilities.includes("app-run")
  )
    return null;

  return (
    <>
      {/* Bubble — a slot in the shell's floating dock, so it stacks with the other
          launchers (agent chat) instead of landing on top of them */}
      <FloatingDockSlot order={FLOATING_DOCK_ORDER.run}>
        <button
          onClick={() => setOpen(true)}
          aria-label={t("open")}
          className={cn(
            "relative flex h-12 w-12 items-center justify-center rounded-full",
            "bg-emerald-600/90 text-white shadow-lg shadow-emerald-950/50",
            "border border-white/[0.12] backdrop-blur-xl",
            "transition-all duration-300 hover:scale-105 hover:bg-emerald-500/90",
            open || !inProject
              ? "pointer-events-none translate-y-4 opacity-0"
              : "translate-y-0 opacity-100"
          )}
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
            <path d="M6 4l14 8-14 8V4z" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {running.length > 0 && (
            <span className="absolute -top-0.5 -right-0.5 flex h-4 min-w-4 items-center justify-center rounded-full border-2 border-primary-950 bg-emerald-400 px-1 text-xs font-bold text-emerald-950">
              {running.length}
            </span>
          )}
          {running.length === 0 && anyFailed && (
            <span
              aria-label={t("startFailed")}
              className="absolute -top-0.5 -right-0.5 h-3.5 w-3.5 rounded-full border-2 border-primary-950 bg-red-400"
            />
          )}
        </button>
      </FloatingDockSlot>

      {/* Live app — the one thing a person wants from a running project: the address. Sits
          just above the bubble while the panel is closed, so it is visible from any page. */}
      {primaryLive && (
        <FloatingDockSlot order={FLOATING_DOCK_ORDER.liveApp}>
          <LiveAppChip
            snapshot={primaryLive}
            className={cn(
              "transition-all duration-300",
              open || !inProject
                ? "pointer-events-none translate-y-4 opacity-0"
                : "translate-y-0 opacity-100"
            )}
          />
        </FloatingDockSlot>
      )}

      <div
        className={cn(
          "fixed right-2 bottom-20 lg:right-5 lg:bottom-5 z-40",
          "flex flex-col overflow-hidden rounded-2xl",
          "w-[min(60rem,calc(100vw-1rem))] h-[min(34rem,calc(100dvh-7rem))]",
          "border border-white/[0.08] bg-surface-sunken/95 backdrop-blur-xl",
          "shadow-2xl shadow-black/60",
          "transition-all duration-300 ease-out",
          open && inProject
            ? "translate-y-0 opacity-100"
            : "pointer-events-none translate-y-[calc(100%+2rem)] opacity-0"
        )}
      >
        {/* Header */}
        <div className="flex items-center gap-2.5 border-b border-white/[0.06] px-3 py-2">
          <svg className="shrink-0 text-emerald-400" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
            <path d="M6 4l14 8-14 8V4z" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <span className="truncate text-sm font-medium text-white">{t("title")}</span>
          {running.length > 0 && (
            <span className="rounded-md bg-emerald-500/15 px-1.5 py-0.5 text-label text-emerald-300">
              {t("runningCount", { count: running.length })}
            </span>
          )}
          {primaryLive && <LiveAppChip snapshot={primaryLive} compact />}

          <div className="ml-auto flex shrink-0 items-center gap-1.5">
            {currentProject && (
              <label
                className="mr-1 hidden items-center gap-1.5 text-xs text-gray-400 sm:flex"
                title={t("keepRunningHint")}
              >
                <span>{t("keepRunning")}</span>
                <Toggle
                  checked={keepRunning}
                  disabled={savingKeepRunning}
                  onChange={(value) => void handleKeepRunning(value)}
                />
              </label>
            )}
            {config?.detected && (
              <Button
                size="sm"
                variant="ghost"
                loading={detecting}
                onClick={() => void handleDetect(true)}
                title={t("redetectHint")}
              >
                {t("redetect")}
              </Button>
            )}
            {running.length > 0 && (
              <Button size="sm" variant="secondary" onClick={() => void stop(null)}>
                {t("stopAll")}
              </Button>
            )}
            {config?.detected && (
              <Button
                size="sm"
                loading={starting}
                disabled={effectiveSelection.length === 0}
                onClick={() => void handleStart(false)}
              >
                {t("run")}
              </Button>
            )}
            <button
              onClick={() => setOpen(false)}
              aria-label={t("hide")}
              className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
                <path d="M6 9l6 6 6-6" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </button>
          </div>
        </div>

        {/* Status strips */}
        {config?.stale && (
          <div className="flex items-center gap-2 border-b border-cyan-500/20 bg-cyan-500/10 px-3 py-2 text-xs text-cyan-200">
            <span className="flex-1">{t("stale")}</span>
            <Button size="sm" variant="ghost" loading={detecting} onClick={() => void handleDetect(true)}>
              {t("redetect")}
            </Button>
          </div>
        )}
        {portBlocked && (
          <div className="flex items-center gap-2 border-b border-amber-500/20 bg-amber-500/10 px-3 py-2 text-xs text-amber-200">
            <span className="flex-1">{t("portBusy")}</span>
            <Button size="sm" variant="secondary" loading={starting} onClick={() => void handleStart(true)}>
              {t("freePortAndRun")}
            </Button>
          </div>
        )}
        {error && (
          <div className="border-b border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-200">
            {error}
          </div>
        )}
        {/* A target that never started has no process output — its log pane would sit empty
            with a red dot and no explanation. Surface the runner's reason here instead. */}
        {activeSnapshot?.status === "failed" && activeSnapshot.error && !activeSnapshot.portConflict && (
          <div className="border-b border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-200">
            <span className="font-medium">{activeSnapshot.label}:</span> {activeSnapshot.error}
          </div>
        )}

        {/* Target picker */}
        {config?.detected && (
          <div className="flex flex-wrap items-center gap-1.5 border-b border-white/[0.06] px-3 py-2">
            {targets.map((target) => {
              const checked = effectiveSelection.includes(target.key);
              const live = running.find((s) => s.key === target.key);
              return (
                <div
                  key={target.key}
                  className={cn(
                    "group flex items-center gap-1.5 rounded-lg border px-2 py-1 text-xs transition-colors",
                    checked
                      ? "border-emerald-500/40 bg-emerald-500/10 text-gray-100"
                      : "border-white/[0.06] bg-white/[0.02] text-gray-400"
                  )}
                >
                  <button
                    onClick={() => toggle(target.key)}
                    className="flex items-center gap-1.5"
                    title={target.command}
                  >
                    <span
                      className={cn(
                        "flex h-3.5 w-3.5 items-center justify-center rounded border",
                        checked ? "border-emerald-400 bg-emerald-500/80" : "border-white/20"
                      )}
                    >
                      {checked && (
                        <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="#022c22" strokeWidth="4">
                          <path d="M5 13l4 4L19 7" strokeLinecap="round" strokeLinejoin="round" />
                        </svg>
                      )}
                    </span>
                    <span className="font-medium">{target.label}</span>
                    <span
                      className={cn(
                        "rounded px-1 py-0.5 text-label",
                        KIND_STYLES[target.kind] ?? KIND_STYLES.other
                      )}
                    >
                      {target.kind}
                    </span>
                    {target.restartOnMerge && (
                      <span className="text-gray-400" title={t("restartsOnMerge")} aria-label={t("restartsOnMerge")}>
                        <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2">
                          <path d="M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5" strokeLinecap="round" strokeLinejoin="round" />
                        </svg>
                      </span>
                    )}
                    {live && <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />}
                  </button>
                  <button
                    onClick={() => setEditing(target)}
                    aria-label={t("editTarget")}
                    className="text-gray-400 opacity-0 transition-opacity group-hover:opacity-100 hover:text-gray-300"
                  >
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <path d="M4 20h4L20 8l-4-4L4 16v4z" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  </button>
                </div>
              );
            })}
          </div>
        )}

        {/* Log tabs */}
        {snapshots.length > 0 && (
          <div className="flex items-center gap-1 overflow-x-auto border-b border-white/[0.06] px-2 py-1.5">
            {snapshots.map((snapshot) => (
              <button
                key={snapshot.runId}
                onClick={() => setActiveRunId(snapshot.runId)}
                className={cn(
                  "flex shrink-0 items-center gap-1.5 rounded-lg px-2 py-1 text-xs transition-colors",
                  snapshot.runId === activeSnapshot?.runId
                    ? "bg-white/[0.08] text-gray-100"
                    : "text-gray-400 hover:bg-white/[0.04] hover:text-gray-300"
                )}
              >
                <StatusDot status={snapshot.status} />
                <span>{snapshot.label}</span>
                {snapshot.url && snapshot.status === "running" && (
                  <a
                    href={snapshot.url}
                    target="_blank"
                    rel="noreferrer"
                    onClick={(e) => e.stopPropagation()}
                    className="text-xs text-emerald-400 underline-offset-2 hover:underline"
                  >
                    :{snapshot.port}
                  </a>
                )}
                {snapshot.status === "running" && (
                  <span
                    role="button"
                    tabIndex={0}
                    aria-label={t("stop")}
                    onClick={(e) => {
                      e.stopPropagation();
                      void stop(snapshot.runId);
                    }}
                    onKeyDown={(e) => {
                      if (e.key === "Enter") void stop(snapshot.runId);
                    }}
                    className="text-gray-400 hover:text-red-400"
                  >
                    <svg width="11" height="11" viewBox="0 0 24 24" fill="currentColor">
                      <rect x="6" y="6" width="12" height="12" rx="2" />
                    </svg>
                  </span>
                )}
              </button>
            ))}
          </div>
        )}

        {/* Body */}
        <div className="min-h-0 flex-1 bg-surface-sunken p-2">
          {activeSnapshot ? (
            <RunLogView
              key={activeSnapshot.runId}
              runId={activeSnapshot.runId}
              scrollback={activeSnapshot.scrollback}
              interactive={activeSnapshot.status === "running"}
              active={open}
              subscribeOutput={subscribeOutput}
              onInput={sendInput}
              onResize={sendResize}
            />
          ) : (
            <EmptyBody
              loading={loading}
              detecting={detecting}
              config={config}
              notes={config?.notes ?? null}
              onDetect={() => void handleDetect(false)}
            />
          )}
        </div>
      </div>

      {editing && (
        <TargetEditor
          target={editing}
          onCancel={() => setEditing(null)}
          onSave={handleSaveTarget}
        />
      )}
    </>
  );
}

function StatusDot({ status }: { status: AppRunSnapshot["status"] }) {
  return (
    <span
      className={cn(
        "h-1.5 w-1.5 shrink-0 rounded-full",
        status === "running"
          ? "bg-emerald-400"
          : status === "failed"
            ? "bg-red-400"
            : "bg-gray-600"
      )}
    />
  );
}

function EmptyBody({
  loading,
  detecting,
  config,
  notes,
  onDetect,
}: {
  loading: boolean;
  detecting: boolean;
  config: ProjectRunConfig | null;
  notes: string | null;
  onDetect: () => void;
}) {
  const t = useTranslations("dashboard.run");

  if (loading) {
    return (
      <div className="flex h-full items-center justify-center text-sm text-gray-400">
        {t("loading")}
      </div>
    );
  }

  if (!config?.detected) {
    return (
      <div className="flex h-full flex-col items-center justify-center gap-3 px-6 text-center">
        <p className="max-w-md text-sm text-gray-400">{t("notDetected")}</p>
        <Button loading={detecting} onClick={onDetect}>
          {t("detect")}
        </Button>
        <p className="max-w-md text-xs text-gray-400">{t("detectOnceHint")}</p>
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col items-center justify-center gap-2 px-6 text-center">
      <p className="text-sm text-gray-400">{t("readyToRun")}</p>
      {config.source === "manifest" && (
        <p className="max-w-lg text-xs text-gray-400">{t("manifestSource")}</p>
      )}
      {notes && <p className="max-w-lg text-xs text-gray-400">{notes}</p>}
    </div>
  );
}

/**
 * The address of the running app, as a link — or "starting…" until its port answers. The
 * whole point of running the project is to look at it, so this is the one element that
 * escapes the panel and sits on every page while something is up.
 */
function LiveAppChip({
  snapshot,
  compact = false,
  className,
}: {
  snapshot: AppRunSnapshot;
  compact?: boolean;
  className?: string;
}) {
  const t = useTranslations("dashboard.run");
  const url = snapshot.previewUrl ?? snapshot.url ?? "";
  const ready = snapshot.ready !== false;
  const shown = url.replace(/^https?:\/\//, "");

  const base = cn(
    "flex h-9 max-w-[16rem] items-center gap-2 rounded-full border px-3 text-xs font-medium",
    "backdrop-blur-xl transition-colors",
    ready
      ? "border-emerald-500/30 bg-emerald-600/20 text-emerald-100 hover:bg-emerald-500/30"
      : "border-white/[0.1] bg-surface-sunken/90 text-gray-300",
    compact && "h-7 px-2.5",
    className
  );

  if (!ready) {
    return (
      <span className={base} title={t("liveApp")} aria-live="polite">
        <span className="h-2 w-2 shrink-0 animate-pulse rounded-full bg-amber-400" />
        <span className="truncate">{t("starting")}</span>
      </span>
    );
  }

  return (
    <a
      href={url}
      target="_blank"
      rel="noreferrer"
      className={base}
      title={t("openLive", { url })}
      aria-label={t("openLive", { url })}
    >
      <span className="h-2 w-2 shrink-0 rounded-full bg-emerald-400" />
      <span className="truncate">{shown}</span>
      <svg className="shrink-0" width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
        <path d="M14 4h6v6M20 4l-9 9M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </a>
  );
}

/**
 * Correcting one target. Detection is very good and occasionally wrong, and a user who
 * cannot fix a single command by hand is stuck with a Run button that does not work.
 */
function TargetEditor({
  target,
  onCancel,
  onSave,
}: {
  target: RunTarget;
  onCancel: () => void;
  onSave: (target: RunTarget) => Promise<void>;
}) {
  const t = useTranslations("dashboard.run");
  const [command, setCommand] = useState(target.command);
  const [workingDirectory, setWorkingDirectory] = useState(target.workingDirectory ?? "");
  const [port, setPort] = useState(target.port?.toString() ?? "");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSave() {
    setSaving(true);
    setError(null);
    try {
      const parsedPort = port.trim() === "" ? null : Number.parseInt(port, 10);
      await onSave({
        ...target,
        command: command.trim(),
        workingDirectory: workingDirectory.trim() === "" ? null : workingDirectory.trim(),
        port: parsedPort !== null && Number.isFinite(parsedPort) ? parsedPort : null,
        url:
          parsedPort !== null && Number.isFinite(parsedPort)
            ? `http://localhost:${parsedPort}`
            : null,
      });
    } catch (e) {
      setError(extractErrorMessage(e, t("saveFailed")));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal open onClose={onCancel} title={target.label}>
      <div className="space-y-4">
        <div className="space-y-1.5">
          <p className="text-sm font-medium text-gray-300">{t("command")}</p>
          <Input
            value={command}
            onChange={(e) => setCommand(e.target.value)}
            placeholder="npm run dev"
            className="text-xs"
          />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1.5">
            <p className="text-sm font-medium text-gray-300">{t("workingDirectory")}</p>
            <Input
              value={workingDirectory}
              onChange={(e) => setWorkingDirectory(e.target.value)}
              placeholder="apps/web"
              className="text-xs"
            />
          </div>
          <div className="space-y-1.5">
            <p className="text-sm font-medium text-gray-300">{t("port")}</p>
            <Input
              value={port}
              onChange={(e) => setPort(e.target.value)}
              placeholder="3000"
              inputMode="numeric"
              className="text-xs"
            />
          </div>
        </div>
        {target.installCommand && (
          <p className="rounded-xl border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs text-gray-400">
            {t("installHint", { command: target.installCommand })}
          </p>
        )}
        {target.manualStep && (
          <p className="rounded-xl border border-amber-500/20 bg-amber-500/[0.06] px-3 py-2 text-xs text-amber-200/80">
            {target.manualStep}
          </p>
        )}
        <p className="rounded-xl border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs text-gray-400">
          {t("secretsHint")}
        </p>
        {error && <p className="text-xs text-red-400">{error}</p>}
        <div className="flex justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onCancel}>
            {t("cancel")}
          </Button>
          <Button size="sm" loading={saving} disabled={command.trim() === ""} onClick={() => void handleSave()}>
            {t("save")}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
