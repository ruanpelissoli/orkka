import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Badge, Button, ConfirmDialog, GlassCard, Input, Modal, Select, Textarea, Toggle, useToast } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { usePermissions } from "@/hooks/use-permissions";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import { cn, formatRelativeTime } from "@/lib/utils";
import type {
  Project,
  ProjectAgent,
  ProjectSettings,
  Schedule,
  ScheduleKind,
  ScheduleRun,
  Skill,
  UpsertScheduleRequest,
} from "@/lib/types";
import {
  browserTimeZone,
  cronToDraft,
  DEFAULT_CRON_DRAFT,
  draftToCron,
  formatUntil,
  humanizeCron,
  WEEKDAY_NAMES,
  type CronDraft,
  type CronPreset,
} from "./cron";
import {
  AUTOMATION_TEMPLATES,
  resolveTemplate,
  type AutomationTemplate,
  type StandingKind,
} from "./templates";

interface AutomationPageProps {
  project: Project;
  initialSchedules: Schedule[];
  agents: ProjectAgent[];
  skills: Skill[];
}

type EditorState =
  | { mode: "closed" }
  | { mode: "create"; kind: StandingKind }
  | { mode: "edit"; schedule: Schedule };

const KIND_ORDER: ScheduleKind[] = ["Playbook", "AutopilotStart", "AutopilotStop", "Reminder"];

const STANDING_KINDS: StandingKind[] = ["Playbook", "AutopilotStart", "AutopilotStop"];

/**
 * Mirrors ScheduleRules.cs. Shown in the UI so the limits read as design rather than as
 * a save that mysteriously fails — keep in sync if the server-side numbers move.
 */
const LIMITS = { maxPerProject: 20, minIntervalMinutes: 15, autoDisableAfterFailures: 5 };

/**
 * The project's Automation page: what a schedule actually does when it fires, the Autonomy
 * master switch, playbooks, Autopilot windows, and agent reminders.
 */
function AutomationPage({ project, initialSchedules, agents, skills }: AutomationPageProps) {
  const t = useTranslations("dashboard.automationPage");
  const withToken = useClientApi();
  const { toast } = useToast();
  const { has } = usePermissions();
  const canExecute = has("TasksExecute");
  const canManage = has("ProjectsManage");

  const [settings, setSettings] = useState<ProjectSettings>(project.settings);
  const [savingSettings, setSavingSettings] = useState(false);
  const [schedules, setSchedules] = useState<Schedule[]>(initialSchedules);
  const [editor, setEditor] = useState<EditorState>({ mode: "closed" });
  const [pendingDelete, setPendingDelete] = useState<Schedule | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [runsFor, setRunsFor] = useState<Schedule | null>(null);
  const [addingTemplate, setAddingTemplate] = useState<string | null>(null);

  const automationOn = settings.automationEnabled ?? true;

  const agentName = useCallback((id: string | null) => agents.find((a) => a.id === id)?.name ?? "—", [agents]);
  /** The slug, not the display name: the row shows what the agent is actually sent ("/groom-backlog"). */
  const skillSlug = useCallback((id: string | null) => skills.find((s) => s.id === id)?.slug ?? null, [skills]);

  const saveSettings = useCallback(
    async (patch: Partial<ProjectSettings>) => {
      const previous = settings;
      const next = { ...settings, ...patch };
      setSettings(next);
      setSavingSettings(true);
      try {
        await withToken((token) => api.projects.update(project.id, { settings: next }, token));
      } catch (err) {
        setSettings(previous);
        toast(extractErrorMessage(err, t("saveFailed")), "error");
      } finally {
        setSavingSettings(false);
      }
    },
    [settings, withToken, project.id, toast, t]
  );

  const upsert = useCallback((s: Schedule) => {
    setSchedules((prev) => {
      const next = prev.some((x) => x.id === s.id) ? prev.map((x) => (x.id === s.id ? s : x)) : [...prev, s];
      return next.sort((a, b) => KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind) || a.name.localeCompare(b.name));
    });
  }, []);

  const toggle = useCallback(
    async (s: Schedule, enabled: boolean) => {
      upsert({ ...s, isEnabled: enabled });
      try {
        upsert(await withToken((token) => api.schedules.setEnabled(project.id, s.id, enabled, token)));
      } catch (err) {
        upsert(s);
        toast(extractErrorMessage(err, t("saveFailed")), "error");
      }
    },
    [upsert, withToken, project.id, toast, t]
  );

  const runNow = useCallback(
    async (s: Schedule) => {
      setBusyId(s.id);
      try {
        const run = await withToken((token) => api.schedules.runNow(project.id, s.id, token));
        toast(run.status === "Succeeded" ? t("ranOk") : run.status === "Skipped" ? t("ranSkipped", { reason: run.summary ?? "" }) : t("ranFailed", { error: run.error ?? "" }), run.status === "Succeeded" ? "success" : run.status === "Skipped" ? "info" : "error");
        // Refresh the row (last run, next run) from the server.
        const fresh = await withToken((token) => api.schedules.list(project.id, token));
        setSchedules(fresh);
      } catch (err) {
        toast(extractErrorMessage(err, t("saveFailed")), "error");
      } finally {
        setBusyId(null);
      }
    },
    [withToken, project.id, toast, t]
  );

  const remove = useCallback(async () => {
    if (!pendingDelete) return;
    setBusyId(pendingDelete.id);
    try {
      await withToken((token) => api.schedules.delete(project.id, pendingDelete.id, token));
      setSchedules((prev) => prev.filter((s) => s.id !== pendingDelete.id));
      setPendingDelete(null);
    } catch (err) {
      toast(extractErrorMessage(err, t("saveFailed")), "error");
    } finally {
      setBusyId(null);
    }
  }, [pendingDelete, withToken, project.id, toast, t]);

  const playbooks = useMemo(() => schedules.filter((s) => s.kind === "Playbook"), [schedules]);
  const windows = useMemo(
    () => schedules.filter((s) => s.kind === "AutopilotStart" || s.kind === "AutopilotStop"),
    [schedules]
  );
  const reminders = useMemo(() => schedules.filter((s) => s.kind === "Reminder"), [schedules]);

  /** A start with no stop runs until its cap stops it or nothing is left — worth saying out loud. */
  const unpairedWindow = useMemo(
    () =>
      windows.some((s) => s.kind === "AutopilotStart" && s.isEnabled) &&
      !windows.some((s) => s.kind === "AutopilotStop" && s.isEnabled),
    [windows]
  );

  /**
   * A template drops off the list once any of its schedules exists, and comes back when the
   * row is deleted — the section is a catalogue of what is not set up yet, not a checklist.
   */
  const availableTemplates = useMemo(() => {
    const taken = new Set(schedules.map((s) => s.name.trim().toLowerCase()));
    return AUTOMATION_TEMPLATES.filter(
      (tpl) => !tpl.parts.some((p) => taken.has(t(`templates.${tpl.key}.${p.nameKey}`).trim().toLowerCase()))
    );
  }, [schedules, t]);

  const addTemplate = useCallback(
    async (template: AutomationTemplate) => {
      const { requests, blocked } = resolveTemplate(template, agents, skills, browserTimeZone(), (key, suffix) =>
        t(`templates.${key}.${suffix}`)
      );
      if (blocked) {
        toast(t("templateNeedsAgent"), "error");
        return;
      }
      setAddingTemplate(template.key);
      try {
        // Sequential: a pair's stop is meaningless without its start, and the per-project
        // cap is checked server-side per create.
        for (const body of requests) {
          upsert(await withToken((token) => api.schedules.create(project.id, body, token)));
        }
        toast(t("templateAdded"), "success");
      } catch (err) {
        toast(extractErrorMessage(err, t("saveFailed")), "error");
      } finally {
        setAddingTemplate(null);
      }
    },
    [agents, skills, t, toast, upsert, withToken, project.id]
  );

  const renderRow = useCallback(
    (s: Schedule) => (
      <li key={s.id}>
        <ScheduleRow
          schedule={s}
          agentName={agentName(s.agentId)}
          skillSlug={skillSlug(s.skillId)}
          paused={!automationOn}
          canExecute={canExecute}
          busy={busyId === s.id}
          onToggle={(v) => void toggle(s, v)}
          onRun={() => void runNow(s)}
          onEdit={() => setEditor({ mode: "edit", schedule: s })}
          onDelete={() => setPendingDelete(s)}
          onRuns={() => setRunsFor(s)}
          projectSlug={project.slug}
        />
      </li>
    ),
    [agentName, skillSlug, automationOn, canExecute, busyId, toggle, runNow, project.slug]
  );

  return (
    <div className="space-y-6">
      {/* What a schedule actually does when it fires. */}
      <GlassCard padding="md" data-testid="how-it-works-card">
        <h3 className="font-display text-sm font-semibold text-white">{t("howTitle")}</h3>
        <ol className="mt-3 grid gap-3 sm:grid-cols-3">
          {(["1", "2", "3"] as const).map((n) => (
            <li key={n} className="flex gap-2.5">
              <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-white/[0.06] text-label font-semibold text-gray-300">
                {n}
              </span>
              <div className="min-w-0">
                <p className="text-xs font-medium text-gray-200">
                  {t(`howStep${n}Title` as "howStep1Title" | "howStep2Title" | "howStep3Title")}
                </p>
                <p className="mt-0.5 text-xs text-gray-400">
                  {t(`howStep${n}Body` as "howStep1Body" | "howStep2Body" | "howStep3Body")}
                </p>
              </div>
            </li>
          ))}
        </ol>
        <p className="mt-3 border-t border-white/[0.06] pt-3 text-xs text-gray-400">{t("howBoundary")}</p>
      </GlassCard>

      {/* Autonomy master switch */}
      <GlassCard padding="md" data-testid="autonomy-card">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h3 className="font-display text-sm font-semibold text-white">{t("autonomyTitle")}</h3>
            <p className="mt-1 text-xs text-gray-400">{t("autonomyHint")}</p>
            {!automationOn && <p className="mt-2 text-xs text-amber-400">{t("autonomyPaused")}</p>}
          </div>
          <Toggle
            checked={automationOn}
            disabled={!canManage || savingSettings}
            onChange={(v) => void saveSettings({ automationEnabled: v })}
          />
        </div>
      </GlassCard>

      {/* Starter automations: templates, not seeded rows — nothing exists until "Add",
          and what it creates is disabled so the user turns it on deliberately. */}
      {canExecute && availableTemplates.length > 0 && (
        <section className="space-y-3" data-testid="templates-section">
          <div>
            <h3 className="font-display text-sm font-semibold text-white">{t("templatesTitle")}</h3>
            <p className="text-xs text-gray-400">{t("templatesHint")}</p>
          </div>
          <ul className="grid gap-2 sm:grid-cols-2">
            {availableTemplates.map((tpl) => (
              <li key={tpl.key}>
                <GlassCard padding="sm" className="h-full">
                  <div className="flex h-full flex-col gap-2">
                    <div className="flex items-start justify-between gap-2">
                      <h4 className="font-display text-sm font-semibold text-white">
                        {t(`templates.${tpl.key}.title`)}
                      </h4>
                      <Badge size="sm" variant={tpl.kind === "Playbook" ? "primary" : "info"}>
                        {t(`kind${tpl.kind}` as "kindPlaybook" | "kindAutopilotStart")}
                      </Badge>
                    </div>
                    <p className="text-xs text-gray-300">{t(`templates.${tpl.key}.body`)}</p>
                    <p className="text-xs text-gray-400">{tpl.parts.map((p) => humanizeCron(p.cron)).join(" → ")}</p>
                    <div className="mt-auto pt-1">
                      <Button
                        size="sm"
                        variant="secondary"
                        disabled={addingTemplate !== null}
                        loading={addingTemplate === tpl.key}
                        onClick={() => void addTemplate(tpl)}
                      >
                        {t("templateAdd")}
                      </Button>
                    </div>
                  </div>
                </GlassCard>
              </li>
            ))}
          </ul>
          <p className="text-xs text-gray-400">{t("templatesFootnote")}</p>
        </section>
      )}

      {/* Playbooks — an agent takes a scheduled turn */}
      <section className="space-y-3" data-testid="playbooks-section">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h3 className="font-display text-sm font-semibold text-white">{t("playbooksTitle")}</h3>
            <p className="text-xs text-gray-400">{t("playbooksHint")}</p>
          </div>
          <RequirePermission permission="TasksExecute">
            <Button size="sm" onClick={() => setEditor({ mode: "create", kind: "Playbook" })}>
              {t("newPlaybook")}
            </Button>
          </RequirePermission>
        </div>

        {playbooks.length === 0 ? (
          <GlassCard padding="lg">
            <p className="text-center text-sm text-gray-300">{t("playbooksEmpty")}</p>
            <p className="mt-1 text-center text-xs text-gray-400">{t("playbooksEmptyHint")}</p>
          </GlassCard>
        ) : (
          <ul className="space-y-2" data-testid="playbook-list">
            {playbooks.map(renderRow)}
          </ul>
        )}
      </section>

      {/* Autopilot windows — the shift during which queued tasks actually run */}
      <section className="space-y-3" data-testid="windows-section">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h3 className="font-display text-sm font-semibold text-white">{t("windowsTitle")}</h3>
            <p className="text-xs text-gray-400">{t("windowsHint")}</p>
          </div>
          <RequirePermission permission="TasksExecute">
            <Button size="sm" variant="secondary" onClick={() => setEditor({ mode: "create", kind: "AutopilotStart" })}>
              {t("newWindow")}
            </Button>
          </RequirePermission>
        </div>

        {windows.length === 0 ? (
          <GlassCard padding="lg">
            <p className="text-center text-sm text-gray-300">{t("windowsEmpty")}</p>
            <p className="mt-1 text-center text-xs text-gray-400">{t("windowsEmptyHint")}</p>
          </GlassCard>
        ) : (
          <>
            {unpairedWindow && (
              <p className="text-xs text-amber-400" data-testid="unpaired-window">
                {t("windowsUnpaired")}
              </p>
            )}
            <ul className="space-y-2" data-testid="window-list">
              {windows.map(renderRow)}
            </ul>
          </>
        )}
      </section>

      {/* Agent reminders */}
      {reminders.length > 0 && (
        <section className="space-y-3">
          <div>
            <h3 className="font-display text-sm font-semibold text-white">{t("remindersTitle")}</h3>
            <p className="text-xs text-gray-400">{t("remindersHint")}</p>
          </div>
          <ul className="space-y-2">
            {reminders.map((s) => (
              <li key={s.id}>
                <GlassCard padding="sm" className={cn(!s.isEnabled && "opacity-60")}>
                  <div className="flex items-center justify-between gap-3">
                    <div className="min-w-0">
                      <p className="truncate text-sm text-gray-200">
                        <span className="text-gray-400">{agentName(s.agentId)}:</span> {s.message}
                      </p>
                      <p className="text-xs text-gray-400">
                        {s.nextRunAt ? t("firesAt", { when: new Date(s.nextRunAt).toLocaleString() }) : t("reminderDone")}
                      </p>
                    </div>
                    <RequirePermission permission="TasksExecute">
                      <Button size="sm" variant="ghost" className="text-red-400" onClick={() => setPendingDelete(s)}>
                        {t("delete")}
                      </Button>
                    </RequirePermission>
                  </div>
                </GlassCard>
              </li>
            ))}
          </ul>
        </section>
      )}

      {/* The limits the server enforces, before a save hits one of them. */}
      <p className="text-xs leading-relaxed text-gray-400" data-testid="guardrails">
        {t("guardrails", {
          max: LIMITS.maxPerProject,
          minutes: LIMITS.minIntervalMinutes,
          failures: LIMITS.autoDisableAfterFailures,
        })}
      </p>

      {editor.mode !== "closed" && (
        <ScheduleEditor
          projectId={project.id}
          agents={agents}
          skills={skills}
          schedule={editor.mode === "edit" ? editor.schedule : null}
          initialKind={editor.mode === "create" ? editor.kind : "Playbook"}
          canUncap={has("BillingManage")}
          onClose={() => setEditor({ mode: "closed" })}
          onSaved={(s) => {
            upsert(s);
            setEditor({ mode: "closed" });
          }}
        />
      )}

      {runsFor && <RunsDrawer projectId={project.id} projectSlug={project.slug} schedule={runsFor} onClose={() => setRunsFor(null)} />}

      <ConfirmDialog
        open={pendingDelete !== null}
        title={t("deleteTitle")}
        message={t("deleteMessage", { name: pendingDelete?.name ?? "" })}
        confirmLabel={t("delete")}
        cancelLabel={t("cancel")}
        danger
        pending={busyId !== null}
        onConfirm={() => void remove()}
        onCancel={() => setPendingDelete(null)}
      />
    </div>
  );
}

function ScheduleRow({
  schedule: s,
  agentName,
  skillSlug,
  paused,
  canExecute,
  busy,
  onToggle,
  onRun,
  onEdit,
  onDelete,
  onRuns,
  projectSlug,
}: {
  schedule: Schedule;
  agentName: string;
  skillSlug: string | null;
  paused: boolean;
  canExecute: boolean;
  busy: boolean;
  onToggle: (v: boolean) => void;
  onRun: () => void;
  onEdit: () => void;
  onDelete: () => void;
  onRuns: () => void;
  projectSlug: string;
}) {
  const t = useTranslations("dashboard.automationPage");
  const when = s.cronExpression ? `${humanizeCron(s.cronExpression)} · ${s.timeZoneId}` : s.fireAt ? t("onceAt", { when: new Date(s.fireAt).toLocaleString() }) : "";
  const what =
    s.kind === "Playbook"
      ? `${agentName}${skillSlug ? ` · /${skillSlug}` : ""}${s.message ? ` · “${s.message.length > 60 ? s.message.slice(0, 60) + "…" : s.message}”` : ""}`
      : s.kind === "AutopilotStart"
        ? s.autopilotCapUsd != null
          ? t("autopilotStartCapped", { cap: s.autopilotCapUsd })
          : t("autopilotStartUncapped")
        : t("autopilotStopWhat");

  return (
    <GlassCard padding="sm" className={cn((!s.isEnabled || paused) && "opacity-70")} data-testid="schedule-row">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h4 className="font-display text-sm font-semibold text-white">{s.name}</h4>
            <Badge size="sm" variant={s.kind === "Playbook" ? "primary" : "info"}>
              {t(`kind${s.kind}` as "kindPlaybook" | "kindAutopilotStart" | "kindAutopilotStop" | "kindReminder")}
            </Badge>
            {s.lastRunStatus && (
              <Badge size="sm" variant={s.lastRunStatus === "Succeeded" ? "success" : s.lastRunStatus === "Failed" ? "error" : s.lastRunStatus === "Running" ? "warning" : "default"}>
                {t(`status${s.lastRunStatus}` as "statusRunning" | "statusSucceeded" | "statusFailed" | "statusSkipped")}
              </Badge>
            )}
            {s.consecutiveFailures > 0 && (
              <span className="text-xs text-red-400">{t("failures", { count: s.consecutiveFailures })}</span>
            )}
          </div>
          <p className="mt-1 truncate text-xs text-gray-300">{what}</p>
          <p className="mt-0.5 text-xs text-gray-400">
            {when}
            {s.isEnabled && s.nextRunAt && !paused && (
              <>
                {" · "}
                {t("nextRun", { when: formatUntil(s.nextRunAt) })}
              </>
            )}
            {s.lastRunAt && (
              <>
                {" · "}
                {t("lastRun", { when: formatRelativeTime(s.lastRunAt) })}
              </>
            )}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          {s.conversationId && s.agentId && (
            <Link
              href={`/p/${projectSlug}/team?agent=${s.agentId}&tab=chat&c=${s.conversationId}`}
              className="rounded-lg px-2 py-1 text-xs text-gray-400 hover:bg-white/[0.06] hover:text-gray-200"
            >
              {t("openConversation")}
            </Link>
          )}
          <Button size="sm" variant="ghost" onClick={onRuns}>
            {t("runs")}
          </Button>
          {canExecute && (
            <>
              <Button size="sm" variant="ghost" onClick={onRun} disabled={busy} loading={busy}>
                {t("runNow")}
              </Button>
              <Button size="sm" variant="ghost" onClick={onEdit}>
                {t("edit")}
              </Button>
              <Button size="sm" variant="ghost" className="text-red-400" onClick={onDelete}>
                {t("delete")}
              </Button>
              <Toggle checked={s.isEnabled} onChange={onToggle} disabled={busy} />
            </>
          )}
        </div>
      </div>
    </GlassCard>
  );
}

function ScheduleEditor({
  projectId,
  agents,
  skills,
  schedule,
  initialKind,
  canUncap,
  onClose,
  onSaved,
}: {
  projectId: string;
  agents: ProjectAgent[];
  skills: Skill[];
  schedule: Schedule | null;
  initialKind: StandingKind;
  canUncap: boolean;
  onClose: () => void;
  onSaved: (s: Schedule) => void;
}) {
  const t = useTranslations("dashboard.automationPage");
  const withToken = useClientApi();

  const [name, setName] = useState(schedule?.name ?? "");
  const [kind, setKind] = useState<StandingKind>(
    schedule && schedule.kind !== "Reminder" ? schedule.kind : initialKind
  );
  const [timing, setTiming] = useState<"recurring" | "once">(schedule?.fireAt ? "once" : "recurring");
  const [cron, setCron] = useState<CronDraft>(schedule?.cronExpression ? cronToDraft(schedule.cronExpression) : DEFAULT_CRON_DRAFT);
  const [timeZone, setTimeZone] = useState(schedule?.timeZoneId ?? browserTimeZone());
  const [fireAt, setFireAt] = useState(schedule?.fireAt ? toLocalInput(schedule.fireAt) : "");
  const [agentId, setAgentId] = useState(schedule?.agentId ?? agents[0]?.id ?? "");
  const [skillId, setSkillId] = useState(schedule?.skillId ?? "");
  const [message, setMessage] = useState(schedule?.message ?? "");
  const [cap, setCap] = useState(schedule?.autopilotCapUsd != null ? String(schedule.autopilotCapUsd) : "10");
  const [uncapped, setUncapped] = useState(schedule?.kind === "AutopilotStart" && schedule.autopilotCapUsd == null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const enabledSkills = useMemo(() => skills.filter((s) => s.isEnabled), [skills]);
  const cronText = timing === "recurring" ? draftToCron(cron) : "";
  const canSave =
    name.trim().length > 0 &&
    !saving &&
    (timing === "recurring" ? cronText.length > 0 : fireAt.length > 0) &&
    (kind !== "Playbook" || (agentId && (skillId || message.trim()))) &&
    (kind !== "AutopilotStart" || uncapped || Number(cap) > 0);

  const save = useCallback(async () => {
    setSaving(true);
    setError(null);
    const body: UpsertScheduleRequest = {
      name: name.trim(),
      kind,
      cronExpression: timing === "recurring" ? cronText : null,
      timeZoneId: timing === "recurring" ? timeZone : null,
      fireAt: timing === "once" ? new Date(fireAt).toISOString() : null,
      agentId: kind === "Playbook" ? agentId : null,
      skillId: kind === "Playbook" && skillId ? skillId : null,
      message: kind === "Playbook" ? message.trim() || null : null,
      autopilotCapUsd: kind === "AutopilotStart" && !uncapped ? Number(cap) : null,
      uncapped: kind === "AutopilotStart" && uncapped,
      isEnabled: schedule?.isEnabled ?? true,
    };
    try {
      const saved = await withToken((token) =>
        schedule ? api.schedules.update(projectId, schedule.id, body, token) : api.schedules.create(projectId, body, token)
      );
      onSaved(saved);
    } catch (err) {
      setError(extractErrorMessage(err, t("saveFailed")));
    } finally {
      setSaving(false);
    }
  }, [name, kind, timing, cronText, timeZone, fireAt, agentId, skillId, message, cap, uncapped, schedule, withToken, projectId, onSaved, t]);

  return (
    <Modal open onClose={onClose} title={schedule ? t("editTitle") : t("createTitle")} className="max-w-2xl">
      <div className="space-y-4">
        <Input label={t("fieldName")} value={name} onChange={(e) => setName(e.target.value)} maxLength={120} autoFocus />

        {/* Kind as cards, not a bare select: the three options do very different things and
            the consequence has to be readable before the schedule exists. */}
        <div>
          <span className="mb-1.5 block text-sm font-medium text-gray-300">{t("fieldKind")}</span>
          <div className="grid gap-2 sm:grid-cols-3" role="radiogroup" aria-label={t("fieldKind")}>
            {STANDING_KINDS.map((k) => (
              <button
                key={k}
                type="button"
                role="radio"
                aria-checked={kind === k}
                onClick={() => setKind(k)}
                className={cn(
                  "rounded-lg border p-2.5 text-left transition-colors",
                  kind === k
                    ? "border-primary-400/50 bg-primary-400/[0.08]"
                    : "border-white/[0.08] bg-white/[0.02] hover:bg-white/[0.05]"
                )}
              >
                <span className={cn("block text-xs font-medium", kind === k ? "text-white" : "text-gray-300")}>
                  {t(`kind${k}` as "kindPlaybook" | "kindAutopilotStart" | "kindAutopilotStop")}
                </span>
                <span className="mt-0.5 block text-label leading-snug text-gray-400">
                  {t(`kind${k}Desc` as "kindPlaybookDesc" | "kindAutopilotStartDesc" | "kindAutopilotStopDesc")}
                </span>
              </button>
            ))}
          </div>
        </div>

        {kind === "Playbook" && (
          <>
            <Select label={t("fieldAgent")} value={agentId} onChange={(e) => setAgentId(e.target.value)}>
              {agents.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </Select>
            <div>
              <Select label={t("fieldSkill")} value={skillId} onChange={(e) => setSkillId(e.target.value)}>
                <option value="">{t("noSkill")}</option>
                {enabledSkills.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name} (/{s.slug})
                  </option>
                ))}
              </Select>
              <p className="mt-1 text-xs text-gray-400">{t("fieldSkillHint")}</p>
            </div>
            <div>
              <Textarea
                label={t("fieldMessage")}
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                rows={3}
                maxLength={4000}
                placeholder={t("fieldMessagePlaceholder")}
              />
              <p className="mt-1 text-xs text-gray-400">{t("fieldMessageHint")}</p>
            </div>
          </>
        )}

        {kind === "AutopilotStart" && (
          <div className="space-y-2">
            <Input
              label={t("fieldCap")}
              type="number"
              min={1}
              step={1}
              value={cap}
              disabled={uncapped}
              onChange={(e) => setCap(e.target.value)}
            />
            <p className="text-xs text-gray-400">{t("fieldCapHint")}</p>
            {canUncap && (
              <label className="flex items-center gap-2 text-xs text-gray-300">
                <input type="checkbox" checked={uncapped} onChange={(e) => setUncapped(e.target.checked)} />
                {t("uncapped")}
              </label>
            )}
            {uncapped && <p className="text-xs text-amber-400">{t("uncappedHint")}</p>}
          </div>
        )}

        {kind === "AutopilotStop" && <p className="text-xs text-gray-400">{t("stopPairHint")}</p>}

        {/* Timing */}
        <div>
          <span className="mb-1.5 block text-sm font-medium text-gray-300">{t("fieldWhen")}</span>
          <div className="mb-2 inline-flex rounded-lg border border-white/[0.08] bg-white/[0.03] p-0.5 text-xs">
            {(["recurring", "once"] as const).map((v) => (
              <button
                key={v}
                type="button"
                onClick={() => setTiming(v)}
                className={cn("rounded-md px-2.5 py-1", timing === v ? "bg-white/[0.08] text-white" : "text-gray-400 hover:text-gray-200")}
              >
                {v === "recurring" ? t("recurring") : t("once")}
              </button>
            ))}
          </div>
          {timing === "recurring" ? (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <Select value={cron.preset} onChange={(e) => setCron({ ...cron, preset: e.target.value as CronPreset })}>
                <option value="hourly">{t("presetHourly")}</option>
                <option value="daily">{t("presetDaily")}</option>
                <option value="weekdays">{t("presetWeekdays")}</option>
                <option value="weekly">{t("presetWeekly")}</option>
                <option value="custom">{t("presetCustom")}</option>
              </Select>
              {cron.preset === "custom" ? (
                <Input value={cron.raw} onChange={(e) => setCron({ ...cron, raw: e.target.value })} placeholder="0 9 * * 1-5" className="font-mono" />
              ) : cron.preset === "hourly" ? (
                <div />
              ) : (
                <div className="flex gap-2">
                  {cron.preset === "weekly" && (
                    <Select value={String(cron.weekday)} onChange={(e) => setCron({ ...cron, weekday: Number(e.target.value) })}>
                      {WEEKDAY_NAMES.map((d, i) => (
                        <option key={d} value={i}>
                          {d}
                        </option>
                      ))}
                    </Select>
                  )}
                  <Input type="time" value={cron.time} onChange={(e) => setCron({ ...cron, time: e.target.value })} />
                </div>
              )}
              <Input label={t("fieldTimeZone")} value={timeZone} onChange={(e) => setTimeZone(e.target.value)} className="sm:col-span-2" />
              <p className="text-xs text-gray-400 sm:col-span-2">
                {t("cronPreview", { cron: cronText, human: humanizeCron(cronText) })}
              </p>
              {kind === "Playbook" && (
                <p className="text-xs text-gray-400 sm:col-span-2">
                  {t("fieldWhenHint", { minutes: LIMITS.minIntervalMinutes })}
                </p>
              )}
            </div>
          ) : (
            <Input type="datetime-local" value={fireAt} onChange={(e) => setFireAt(e.target.value)} />
          )}
        </div>

        {error && <p className="text-sm text-red-400">{error}</p>}

        <div className="flex justify-end gap-2 pt-2">
          <Button variant="ghost" onClick={onClose} disabled={saving}>
            {t("cancel")}
          </Button>
          <Button onClick={() => void save()} disabled={!canSave} loading={saving}>
            {t("save")}
          </Button>
        </div>
      </div>
    </Modal>
  );
}

function RunsDrawer({ projectId, projectSlug, schedule, onClose }: { projectId: string; projectSlug: string; schedule: Schedule; onClose: () => void }) {
  const t = useTranslations("dashboard.automationPage");
  const withToken = useClientApi();
  const [runs, setRuns] = useState<ScheduleRun[] | null>(null);
  const [error, setError] = useState(false);

  // Fetch once on open (the drawer is remounted per schedule).
  useEffect(() => {
    let cancelled = false;
    withToken((token) => api.schedules.runs(projectId, schedule.id, token))
      .then((r) => {
        if (!cancelled) setRuns(r);
      })
      .catch(() => {
        if (!cancelled) setError(true);
      });
    return () => {
      cancelled = true;
    };
  }, [withToken, projectId, schedule.id]);

  return (
    <Modal open onClose={onClose} title={t("runsTitle", { name: schedule.name })} className="max-w-2xl">
      {error ? (
        <p className="text-sm text-red-400">{t("saveFailed")}</p>
      ) : runs === null ? (
        <div className="h-8 animate-pulse rounded bg-white/[0.04]" />
      ) : runs.length === 0 ? (
        <p className="text-sm text-gray-400">{t("noRuns")}</p>
      ) : (
        <ul className="divide-y divide-white/[0.04]">
          {runs.map((r) => (
            <li key={r.id} className="py-2">
              <div className="flex items-center gap-2 text-xs">
                <Badge size="sm" variant={r.status === "Succeeded" ? "success" : r.status === "Failed" ? "error" : r.status === "Running" ? "warning" : "default"}>
                  {t(`status${r.status}` as "statusRunning" | "statusSucceeded" | "statusFailed" | "statusSkipped")}
                </Badge>
                <span className="text-gray-400">{new Date(r.startedAt).toLocaleString()}</span>
                <span className="text-gray-400">· {r.trigger}</span>
                {r.conversationId && schedule.agentId && (
                  <Link href={`/p/${projectSlug}/team?agent=${schedule.agentId}&tab=chat&c=${r.conversationId}`} className="ml-auto text-primary-400 hover:text-primary-300">
                    {t("openConversation")}
                  </Link>
                )}
              </div>
              {(r.summary || r.error) && (
                <p className={cn("mt-1 whitespace-pre-wrap text-xs", r.error ? "text-red-400" : "text-gray-300")}>{r.error ?? r.summary}</p>
              )}
            </li>
          ))}
        </ul>
      )}
    </Modal>
  );
}

function toLocalInput(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export { AutomationPage };
