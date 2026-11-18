import { useState, useMemo, useRef, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { GlassCard, Badge, Button, MentionTextarea, Markdown, ChipSelect } from "@/components/ui";
import { ForgePrLink } from "@/components/pr/ForgePrLink";
import { ExternalTicketBadge } from "./ExternalTicketBadge";
import { usePermissions } from "@/hooks/use-permissions";
import { updateTask } from "@/lib/actions";
import { api, extractErrorMessage } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { useToast } from "@/components/ui";
import { useLabels } from "@/hooks/use-labels";
import {
  LANE_MAP,
  PRIORITIES,
  TASK_TYPES,
  TASK_STATUS_MAP,
  DEV_AGENT_ROLE_KEYS,
} from "@/lib/constants";
import type { Milestone, ProjectAgent, ProjectRepo, Task } from "@/lib/types";
import type { PriorityKey, TaskTypeKey } from "@/lib/constants";

interface TaskDetailEditableProps {
  task: Task;
  projectId: string;
  onActionComplete?: () => void;
  /** Project milestones. Empty/omitted hides the picker — a project with no roadmap
   * shouldn't be asked which milestone a task belongs to. */
  milestones?: Milestone[];
  /** Project repos. A task with no repo cannot be submitted, and tasks can arrive that way
   * (planner-created, or the repo was unlinked), so detail has to be able to set one. */
  repos?: ProjectRepo[];
  /** Project agents — dev roles become the optional "assign to" choices, matching the
   * server guard that only lets dev agents be pinned. */
  agents?: ProjectAgent[];
}

function TaskDetailEditable({ task, projectId, onActionComplete, milestones = [], repos = [], agents = [] }: TaskDetailEditableProps) {
  const t = useTranslations("dashboard");
  const labels = useLabels();
  const { has } = usePermissions();
  const canEdit = has("TasksEdit");
  const [editingTitle, setEditingTitle] = useState(false);
  const [editingDesc, setEditingDesc] = useState(false);
  const [title, setTitle] = useState(task.title);
  const [description, setDescription] = useState(task.description);
  const [contextFiles, setContextFiles] = useState<string[]>(task.contextFiles ?? []);
  const titleRef = useRef<HTMLInputElement>(null);
  const [isPending, startTransition] = useTransition();

  // Only dev agents can be pinned — AgentAssigner honors the pin verbatim for
  // Planning/Developing, so offering a PM or Designer here would just 400 on save.
  const devAgents = useMemo(
    () => agents.filter((a) => DEV_AGENT_ROLE_KEYS.has(a.role)),
    [agents]
  );

  // Local state is what renders — the board modal feeds `task` from a client
  // fetch that a save does not re-run, so edits would visually vanish on blur
  // if the prop were rendered directly. The "last prop" values below adopt
  // external edits (PM refinement, teammates via refresh) without clobbering an
  // in-progress or just-saved local edit: only a change to the prop itself is
  // copied in, and only while that field is not being edited.
  const [lastTitleProp, setLastTitleProp] = useState(task.title);
  const [lastDescProp, setLastDescProp] = useState(task.description);
  const [lastFilesProp, setLastFilesProp] = useState(() => JSON.stringify(task.contextFiles ?? []));
  if (task.title !== lastTitleProp) {
    setLastTitleProp(task.title);
    if (!editingTitle) setTitle(task.title);
  }
  if (task.description !== lastDescProp) {
    setLastDescProp(task.description);
    if (!editingDesc) setDescription(task.description);
  }
  const incomingFiles = JSON.stringify(task.contextFiles ?? []);
  if (incomingFiles !== lastFilesProp) {
    setLastFilesProp(incomingFiles);
    if (!editingDesc) setContextFiles(task.contextFiles ?? []);
  }

  const lane = LANE_MAP[task.currentLane];
  const priority = PRIORITIES.find((p) => p.key === task.priority);
  const taskType = TASK_TYPES.find((t) => t.key === task.type);
  const status = TASK_STATUS_MAP[task.status];
  const withToken = useClientApi();
  const { toast } = useToast();

  // The chip pickers call the API directly rather than through the updateTask action, so the
  // catch below gets the ApiError itself and can show the API's real message. (When updateTask
  // was a Next server action its thrown errors were redacted in production, and the select
  // simply snapped back with no explanation.)
  async function applyUpdate(
    data: import("@/lib/types").UpdateTaskRequest,
    onFailure?: () => void
  ) {
    try {
      await withToken((token) => api.tasks.update(task.id, data, token));
      onActionComplete?.();
    } catch (e) {
      toast(extractErrorMessage(e), "error");
      onFailure?.();
    }
  }

  function saveField(field: string, value: string) {
    startTransition(async () => {
      try {
        await updateTask(task.id, { [field]: value });
        onActionComplete?.();
      } catch {
        if (field === "title") setTitle(task.title);
        if (field === "description") setDescription(task.description);
      }
    });
  }

  function saveFields(data: import("@/lib/types").UpdateTaskRequest) {
    startTransition(async () => {
      try {
        await updateTask(task.id, data);
        onActionComplete?.();
      } catch {
        setDescription(task.description);
        setContextFiles(task.contextFiles ?? []);
      }
    });
  }

  function handleTitleBlur() {
    setEditingTitle(false);
    if (title !== task.title && title.trim()) {
      saveField("title", title);
    } else {
      setTitle(task.title);
    }
  }

  function handleDescBlur() {
    setEditingDesc(false);
    const descChanged = description !== task.description;
    const filesChanged =
      JSON.stringify(contextFiles) !== JSON.stringify(task.contextFiles ?? []);
    if (descChanged || filesChanged) {
      saveFields({
        ...(descChanged ? { description } : {}),
        ...(filesChanged ? { contextFiles } : {}),
      });
    }
  }

  function cancelDescEdit() {
    setDescription(task.description);
    setContextFiles(task.contextFiles ?? []);
    setEditingDesc(false);
  }

  return (
    <GlassCard padding="lg" accentColor={lane?.color}>
      <div className="space-y-4">
        {/* Task code */}
        <span className="font-mono text-xs text-gray-400">{task.code}</span>

        {/* Title */}
        {editingTitle ? (
          <input
            ref={titleRef}
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            onBlur={handleTitleBlur}
            onKeyDown={(e) => {
              if (e.key === "Enter") handleTitleBlur();
              if (e.key === "Escape") {
                // Don't let the Modal's window listener close the dialog
                e.stopPropagation();
                setTitle(task.title);
                setEditingTitle(false);
              }
            }}
            className="w-full bg-transparent font-display text-xl font-bold text-white outline-none border-b border-primary-500/50 pb-1 md:text-2xl"
            autoFocus
          />
        ) : (
          <h1
            className={cn(
              "font-display text-xl font-bold text-white md:text-2xl",
              canEdit && "cursor-text hover:text-gray-100 transition-colors"
            )}
            onClick={() => {
              if (!canEdit) return;
              setEditingTitle(true);
              setTimeout(() => titleRef.current?.focus(), 0);
            }}
          >
            {title}
          </h1>
        )}

        {/* Meta pills */}
        <div className="flex flex-wrap gap-2">
          <Badge
            style={{
              backgroundColor: `${lane?.color}10`,
              color: lane?.color,
              borderColor: `${lane?.color}20`,
            }}
          >
            {labels.lane(task.currentLane)}
          </Badge>
          {/* Status is a label, not a control — un-blocking lives on the Re-queue
              button in TaskActions, where an action is discoverable. */}
          {status && (
            <Badge
              dot
              dotColor={status.color}
              style={{
                backgroundColor: `${status.color}10`,
                color: status.color,
                borderColor: `${status.color}20`,
              }}
            >
              {labels.taskStatus(task.status)}
            </Badge>
          )}
          {priority && !canEdit && (
            <Badge
              style={{
                backgroundColor: `${priority.color}10`,
                color: priority.color,
                borderColor: `${priority.color}20`,
              }}
            >
              {labels.priority(task.priority)}
            </Badge>
          )}
          {priority && canEdit && (
            <ChipSelect
              label={t("taskDetailEditable.priorityLabel")}
              value={task.priority}
              options={PRIORITIES.map((p) => ({ value: p.key, label: labels.priority(p.key) }))}
              onChange={(v) => applyUpdate({ priority: v as PriorityKey })}
            />
          )}
          {taskType && !canEdit && (
            <Badge variant="default">{labels.taskType(task.type)}</Badge>
          )}
          {taskType && canEdit && (
            <ChipSelect
              label={t("taskDetailEditable.typeLabel")}
              value={task.type}
              options={TASK_TYPES.map((x) => ({ value: x.key, label: labels.taskType(x.key) }))}
              onChange={(v) => applyUpdate({ type: v as TaskTypeKey })}
            />
          )}
          {repos.length > 0 && canEdit && (
            <ChipSelect
              label={t("taskDetailEditable.repositoryLabel")}
              value={task.repoId ?? ""}
              options={repos.map((r) => ({ value: r.id, label: r.name }))}
              placeholder={t("taskDetailEditable.noRepository")}
              // A task with no repo cannot be submitted at all, so this empty state is a
              // blocker rather than a neutral gap.
              emphasizeEmpty
              onChange={(v) => {
                if (v) applyUpdate({ repoId: v });
              }}
            />
          )}
          {devAgents.length > 0 && canEdit && (
            <ChipSelect
              label={t("taskDetailEditable.agentLabel")}
              value={task.assignedAgentId ?? ""}
              options={devAgents.map((a) => ({ value: a.id, label: a.name }))}
              placeholder={t("taskDetailEditable.autoAssignAgent")}
              // "" clears the pin back to auto-assign — the API reads an empty string that
              // way, and omitted as "leave alone".
              onChange={(v) => applyUpdate({ assignedAgentId: v })}
            />
          )}
          {milestones.length > 0 && canEdit && (
            <ChipSelect
              label={t("taskMilestone.label")}
              value={task.milestoneId ?? ""}
              options={milestones.map((m) => ({ value: m.id, label: m.title }))}
              placeholder={t("taskMilestone.none")}
              onChange={(v) => applyUpdate({ milestoneId: v })}
            />
          )}
          {task.prUrl && (
            <>
              <button
                onClick={() => {
                  const el = document.querySelector("[data-pr-panel]");
                  if (el) {
                    el.scrollIntoView({ behavior: "smooth", block: "start" });
                  }
                }}
                className="inline-flex items-center gap-1 rounded-full border border-cyan-500/20 bg-cyan-500/10 px-2.5 py-0.5 text-xs font-medium text-cyan-400 transition-colors hover:bg-cyan-500/20 cursor-pointer"
              >
                <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
                  <path d="M7.177 3.073L9.573.677A.25.25 0 0110 .854v4.792a.25.25 0 01-.427.177L7.177 3.427a.25.25 0 010-.354zM3.75 2.5a.75.75 0 100 1.5.75.75 0 000-1.5zm-2.25.75a2.25 2.25 0 113 2.122v5.256a2.251 2.251 0 11-1.5 0V5.372A2.25 2.25 0 011.5 3.25zM11 2.5h-1V4h1a1 1 0 011 1v5.628a2.251 2.251 0 101.5 0V5A2.5 2.5 0 0011 2.5zm1 10.25a.75.75 0 111.5 0 .75.75 0 01-1.5 0zM3.75 12a.75.75 0 100 1.5.75.75 0 000-1.5z" />
                </svg>
                {t("taskDetailEditable.reviewPr")}
              </button>
            </>
          )}
          {/* task.prUrl is Orkka's own review record; once the work lands on a remote repo
              the developer's runner publishes it as a forge PR — that is the link to follow. */}
          {task.publishedPrUrl && <ForgePrLink url={task.publishedPrUrl} />}
          {/* The tracker ticket this task was imported from, and its epic. */}
          {task.externalKey && (
            <ExternalTicketBadge
              externalKey={task.externalKey}
              externalUrl={task.externalUrl}
              variant="detail"
            />
          )}
          {task.externalEpic && (
            <span
              className="inline-flex items-center rounded-full border border-white/[0.06] px-2.5 py-0.5 text-xs text-gray-400"
              data-testid="external-epic"
            >
              {t("externalTicket.epic", { epic: task.externalEpic })}
            </span>
          )}
          {/* Orkka gave up moving the ticket's status — say what failed and where to fix it. */}
          {task.externalSyncError && (
            <div
              className="w-full rounded-lg border border-amber-500/20 bg-amber-500/[0.06] px-3 py-2 text-xs"
              data-testid="external-sync-error"
            >
              <p className="text-amber-300">{task.externalSyncError}</p>
              <p className="mt-0.5 text-gray-400">
                {t("externalTicket.syncHint")}{" "}
                <Link
                  href={`/p/${projectId}/settings#ticket-source`}
                  className="text-amber-300 underline underline-offset-2 hover:text-amber-200"
                >
                  {t("externalTicket.syncHintLink")}
                </Link>
              </p>
            </div>
          )}
        </div>

        {/* Merge failure banner — cleared automatically once the PR merges */}
        {task.mergeFailureReason && (
          <div className="flex items-start gap-2.5 rounded-xl border border-amber-500/20 bg-amber-500/[0.06] px-3 py-2.5">
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" className="mt-0.5 shrink-0 text-amber-400">
              <path d="M8 5.5v3M8 11h.01M6.86 2.57L1.21 12.4a1.3 1.3 0 001.14 1.95h11.3a1.3 1.3 0 001.14-1.95L9.14 2.57a1.3 1.3 0 00-2.28 0z" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            <div className="min-w-0">
              <p className="text-xs font-semibold text-amber-400">
                {t("taskDetailEditable.mergeFailed")}
              </p>
              <p className="mt-0.5 text-xs leading-relaxed text-amber-200/70 break-words">
                {task.mergeFailureReason}
              </p>
              <p className="mt-0.5 text-xs text-gray-400">
                {t("taskDetailEditable.mergeFailedHint")}
              </p>
              <Link
                href={`/p/${projectId}/tasks/${task.number}/merge`}
                className="mt-1.5 inline-block text-xs font-semibold text-amber-300 hover:text-amber-200"
              >
                {t("taskDetailEditable.mergeFix")} →
              </Link>
            </div>
          </div>
        )}

        {/* Description */}
        {editingDesc ? (
          <div
            onKeyDown={(e) => {
              if (e.key !== "Escape") return;
              // Keep Escape inside the editor — the Modal's window listener
              // would otherwise close the whole dialog mid-edit.
              e.stopPropagation();
              // Already consumed to close the @-mention dropdown
              if (e.defaultPrevented) return;
              cancelDescEdit();
            }}
          >
            <MentionTextarea
              value={description}
              onChange={setDescription}
              contextFiles={contextFiles}
              onContextFilesChange={setContextFiles}
              projectId={projectId}
              repoId={task.repoId}
              autoFocus
              onBlur={handleDescBlur}
              className="bg-transparent border-primary-500/30 min-h-[100px]"
            />
            {/* mousedown preventDefault keeps textarea focus so blur-save
                doesn't race the click */}
            <div className="mt-2 flex items-center gap-1.5">
              <Button
                size="sm"
                disabled={isPending}
                onMouseDown={(e) => e.preventDefault()}
                onClick={handleDescBlur}
              >
                {t("taskDetailEditable.save")}
              </Button>
              <Button
                variant="ghost"
                size="sm"
                onMouseDown={(e) => e.preventDefault()}
                onClick={cancelDescEdit}
              >
                {t("taskDetailEditable.cancel")}
              </Button>
            </div>
          </div>
        ) : (
          <div>
            <div
              className={cn(
                "rounded-xl p-1 -m-1",
                canEdit && "cursor-text transition-colors hover:bg-white/[0.02]"
              )}
              onClick={(e) => {
                // A markdown link click should open the link, not enter edit mode
                if ((e.target as HTMLElement).closest("a")) return;
                if (canEdit) setEditingDesc(true);
              }}
            >
              {description ? (
                <Markdown className="text-gray-400">{description}</Markdown>
              ) : (
                <p className="text-sm leading-relaxed text-gray-400 italic">
                  {canEdit ? t("taskDetailEditable.clickToAddDescription") : "—"}
                </p>
              )}
            </div>
            {contextFiles.length > 0 && (
              <div className="flex flex-wrap gap-1.5 mt-2">
                {contextFiles.map((file) => (
                  <span
                    key={file}
                    className="inline-flex items-center gap-1 rounded-full border border-primary-500/20 bg-primary-500/10 px-2 py-0.5 text-xs text-primary-400"
                    title={file}
                  >
                    <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                      <path d="M9 1H4a1 1 0 00-1 1v12a1 1 0 001 1h8a1 1 0 001-1V5l-4-4z" strokeLinejoin="round" />
                      <path d="M9 1v4h4" strokeLinejoin="round" />
                    </svg>
                    {file.split("/").pop()}
                  </span>
                ))}
              </div>
            )}
          </div>
        )}

        {/* Tags */}
        {(task.tags?.length ?? 0) > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {task.tags!.map((tag) => (
              <Badge key={tag} size="sm">{tag}</Badge>
            ))}
          </div>
        )}
      </div>
    </GlassCard>
  );
}

export { TaskDetailEditable };
