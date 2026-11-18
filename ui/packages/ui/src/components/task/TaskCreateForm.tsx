import { Fragment, useEffect, useMemo, useState, useRef } from "react";
import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { Button, Input, MentionTextarea, Select, useToast } from "@/components/ui";
import { RepoFilePickerPanel } from "@/components/repos/RepoFilePickerPanel";
import { PRIORITIES, TASK_TYPES, DEV_AGENT_ROLE_KEYS, providerLabel } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import {
  cn,
  groupModelsByProvider,
  latestDocumentVersions,
  providerFromModelId,
} from "@/lib/utils";
import { addTaskDependency, createTaskAndReturn, submitTask } from "@/lib/actions";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { uploadAttachment } from "./use-attachment-upload";
import type {
  Milestone,
  ProjectAgent,
  ProjectDocument,
  ProjectRepo,
  Task,
  TaskForecast,
} from "@/lib/types";
import { billingForModel } from "@/lib/model-billing";
import type {
  AnnotatedModel,
  ModelBilling,
  ModelBillingDecision,
  ModelBillingReason,
  RunnerCoverage,
} from "@/lib/model-billing";
import { TaskForecastSummary } from "@/components/forecast";
import { ModelBillingNote } from "./ModelBillingNote";

/** One choice in the model picker: the model catalog, annotated with whether the runner can
 * run each model (`billing`/`reason`) by `useSelectableModels`. */
export interface TaskModelOption {
  id: string;
  name: string;
  /** AiProvider enum name — drives the <optgroup> headings when present. */
  provider?: string;
  /** Catalog display order — keeps the groups in the same order the API returned. */
  sortOrder?: number;
  /** false = the provider's subscription isn't connected on the runner. */
  connected?: boolean;
  /** Whether the runner can run a task on this model. "unavailable" rows render
   * disabled. Absent rows fall back to connected/not-connected labels. */
  billing?: ModelBilling;
  reason?: ModelBillingReason;
}

/**
 * What the form needs to say, before Create is clicked, whether the user's runner can run the
 * task now or it will wait for a sign-in or for the runner to connect.
 */
export interface TaskBillingContext {
  /** Runner coverage (null while unknown, or when no runner has ever connected). */
  coverage: RunnerCoverage | null;
  /** The annotated catalog — looks up an agent default's readiness without re-deciding it. */
  annotated: readonly AnnotatedModel[];
}

/** Mirrors the backend's `DevAgentSelection.DevRoles` priority: the generalist outranks the
 * specialists, and the niche roles only pick up unassigned tasks when nothing else fits. */
const DEV_ROLE_PRIORITY = ["FullStackDev", "BackendDev", "FrontendDev", "GameDev", "MobileDev"];

/**
 * The dev agent the assigner would choose for a task in `repoId` when none is pinned — the
 * same rule as the backend's `DevAgentSelection.FindDevAgent`, so the billing note describes
 * the run that will actually happen.
 */
function findDefaultDevAgent(agents: readonly ProjectAgent[], repoId: string): ProjectAgent | null {
  const candidates = agents
    .filter((a) => DEV_ROLE_PRIORITY.includes(a.role))
    .sort((a, b) => DEV_ROLE_PRIORITY.indexOf(a.role) - DEV_ROLE_PRIORITY.indexOf(b.role));
  if (candidates.length === 0) return null;
  return (
    (repoId ? candidates.find((a) => a.repoIds.includes(repoId)) : undefined) ??
    candidates.find((a) => a.repoIds.length === 0) ??
    candidates[0]
  );
}

interface BillingNoteState extends ModelBillingDecision {
  provider: string;
  model: string;
}

/**
 * What the note under the picker says, from the runner coverage the page already loaded.
 * Null without a billing context or with no model to judge.
 */
function resolveBillingNote(
  billingContext: TaskBillingContext | undefined,
  effectiveModel: string
): BillingNoteState | null {
  if (!billingContext) return null;
  if (!effectiveModel) return null;
  const known = billingContext.annotated.find(
    (m) => m.id.toLowerCase() === effectiveModel.toLowerCase()
  );
  return {
    ...billingForModel(effectiveModel, billingContext.annotated, billingContext.coverage),
    provider: known?.provider ?? providerFromModelId(effectiveModel),
    model: effectiveModel,
  };
}

/** How long the form waits after the last model/agent change before asking for a forecast. */
const SERVER_LOOKUP_DEBOUNCE_MS = 250;

interface TaskCreateFormProps {
  projectId: string;
  repos: ProjectRepo[];
  /** Project agents — active dev agents become the optional "assign to" choices. */
  agents?: ProjectAgent[];
  /** Selectable models for the per-task override, from the model catalog. Empty/omitted = the
   * catalog is unavailable, so the form offers only "use the agent's default", which is a
   * fully working choice. */
  models?: TaskModelOption[];
  /** Where to go after creating — the page the user came from (defaults to the board). */
  returnTo?: string;
  /** Project milestones. Empty/omitted hides the picker entirely — a project with no
   * roadmap yet shouldn't be asked which milestone a task belongs to. */
  milestones?: Milestone[];
  /** Preselected milestone, e.g. when creating from a roadmap card. */
  defaultMilestoneId?: string;
  /** Enables the "runs on your runner" readiness note under the model picker. */
  billingContext?: TaskBillingContext;
}

function TaskCreateForm({
  projectId,
  repos,
  agents,
  models,
  returnTo,
  milestones = [],
  defaultMilestoneId = "",
  billingContext,
}: TaskCreateFormProps) {
  const t = useTranslations("dashboard");
  const labels = useLabels();
  const router = useRouter();
  const { toast } = useToast();
  const [files, setFiles] = useState<File[]>([]);
  const [pendingIntent, setPendingIntent] = useState<"submit" | "draft" | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [description, setDescription] = useState("");
  const [contextFiles, setContextFiles] = useState<string[]>([]);
  const [selectedRepoId, setSelectedRepoId] = useState("");
  // Repos this one piece of work also changes. Kept out of the primary select on purpose: the
  // task still lands in exactly one repo, and these produce sibling tasks at submission.
  const [spannedRepoIds, setSpannedRepoIds] = useState<string[]>([]);
  const [selectedAgentId, setSelectedAgentId] = useState("");
  const [selectedModel, setSelectedModel] = useState("");
  const [selectedMilestoneId, setSelectedMilestoneId] = useState(defaultMilestoneId);
  // Re-estimated whenever an input that changes the agent time changes (model, agent).
  const [forecast, setForecast] = useState<TaskForecast | null>(null);
  const [panelOpen, setPanelOpen] = useState(false);
  const withToken = useClientApi();
  const [selectedDeps, setSelectedDeps] = useState<Task[]>([]);
  const [depPickerOpen, setDepPickerOpen] = useState(false);
  const [depFilter, setDepFilter] = useState("");
  const [depCandidates, setDepCandidates] = useState<Task[] | null>(null);
  const [selectedDocIds, setSelectedDocIds] = useState<string[]>([]);
  const [docPickerOpen, setDocPickerOpen] = useState(false);
  const [docFilter, setDocFilter] = useState("");
  const [documents, setDocuments] = useState<ProjectDocument[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        const result = await withToken((token) =>
          api.tasks.forecastDraft(
            projectId,
            { model: selectedModel || undefined, assignedAgentId: selectedAgentId || undefined },
            token
          )
        );
        if (!cancelled) setForecast(result);
      } catch {
        // No forecast renders as the "not enough history yet" empty state.
      }
    }, SERVER_LOOKUP_DEBOUNCE_MS);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [withToken, projectId, selectedModel, selectedAgentId]);

  const selectedRepo = repos.find((r) => r.id === selectedRepoId) ?? null;

  // Only dev agents can be pinned to a task (the assigner runs them in
  // Planning/Developing). Prefer repo-scoped matches first so e.g. a Game Dev
  // scoped to the selected repo tops the list.
  const assignableAgents = (agents ?? [])
    .filter((a) => DEV_AGENT_ROLE_KEYS.has(a.role))
    .sort((a, b) =>
      Number(b.repoIds.includes(selectedRepoId)) - Number(a.repoIds.includes(selectedRepoId)) ||
      a.name.localeCompare(b.name)
    );

  const selectedAgent = assignableAgents.find((a) => a.id === selectedAgentId) ?? null;

  const modelOptions = useMemo<TaskModelOption[]>(() => {
    const base = models ?? [];
    // An agent saved on a model the catalog doesn't list (an Ollama tag that is no longer
    // pulled, say) must still render, or the select would show a blank value.
    return selectedModel && !base.some((m) => m.id === selectedModel)
      ? [...base, { id: selectedModel, name: selectedModel }]
      : base;
  }, [models, selectedModel]);

  const modelGroups = useMemo(
    () => groupModelsByProvider(modelOptions.map((m) => ({ ...m, provider: m.provider ?? "" }))),
    [modelOptions]
  );

  // Only send the model when it differs from the chosen agent's own default. An untouched
  // pick stores nothing, so the task keeps tracking whatever the Team page says at run time.
  const modelOverride =
    selectedModel && selectedModel !== selectedAgent?.model ? selectedModel : "";

  // The model the dev run will actually use: the explicit pick, else the pinned agent's
  // model, else whatever dev agent the assigner would choose for this repo. "Use the agent's
  // default" is not "no model" — it still has a provider, and the runner must have that
  // provider signed in to run it.
  const effectiveModel =
    selectedModel ||
    selectedAgent?.model ||
    findDefaultDevAgent(assignableAgents, selectedRepoId)?.model ||
    "";

  // Readiness note: null without a billing context or with no model to judge.
  const billingNote = resolveBillingNote(billingContext, effectiveModel);

  // Dependency candidates load lazily when the picker opens. Done tasks are archived,
  // but a Done-but-unmerged one is still a valid blocker — pull those in too.
  useEffect(() => {
    if (!depPickerOpen || depCandidates !== null) return;
    let cancelled = false;
    Promise.all([
      withToken((token) => api.tasks.list(projectId, token)),
      withToken((token) => api.tasks.listFiltered(projectId, { isArchived: true }, token)),
    ])
      .then(([active, archived]) => {
        if (!cancelled)
          setDepCandidates([
            ...active,
            ...archived.filter((c) => c.status === "Completed" && !c.isMerged),
          ]);
      })
      .catch(() => {
        if (!cancelled) setDepCandidates([]);
      });
    return () => {
      cancelled = true;
    };
  }, [depPickerOpen, depCandidates, projectId, withToken]);

  // Context documents load lazily when the picker opens. Only the newest copy of each
  // folder+title is offered — agents sometimes save a new document instead of revising.
  useEffect(() => {
    if (!docPickerOpen || documents !== null) return;
    let cancelled = false;
    withToken((token) => api.documents.list(projectId, token))
      .then((docs) => {
        if (!cancelled) setDocuments(latestDocumentVersions(docs));
      })
      .catch(() => {
        if (!cancelled) setDocuments([]);
      });
    return () => {
      cancelled = true;
    };
  }, [docPickerOpen, documents, projectId, withToken]);

  const docById = useMemo(
    () => new Map((documents ?? []).map((d) => [d.id, d])),
    [documents]
  );

  const groupedDocs = useMemo(() => {
    const needle = docFilter.trim().toLowerCase();
    const visible = (documents ?? []).filter(
      (d) =>
        !needle ||
        d.title.toLowerCase().includes(needle) ||
        d.path.toLowerCase().includes(needle)
    );
    const groups = new Map<string, ProjectDocument[]>();
    for (const doc of visible) {
      const key = doc.path || "/";
      const list = groups.get(key) ?? [];
      list.push(doc);
      groups.set(key, list);
    }
    return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [documents, docFilter]);

  function toggleDocument(id: string) {
    setSelectedDocIds((prev) =>
      prev.includes(id) ? prev.filter((d) => d !== id) : [...prev, id]
    );
  }

  const visibleDepCandidates = useMemo(() => {
    const needle = depFilter.trim().toLowerCase();
    const selected = new Set(selectedDeps.map((d) => d.id));
    return (depCandidates ?? []).filter(
      (c) =>
        !(c.status === "Completed" && c.isMerged) &&
        c.status !== "Archived" &&
        !selected.has(c.id) &&
        (!needle ||
          c.title.toLowerCase().includes(needle) ||
          c.code.toLowerCase().includes(needle))
    );
  }, [depCandidates, depFilter, selectedDeps]);

  function toggleContextFile(path: string) {
    setContextFiles((prev) =>
      prev.includes(path) ? prev.filter((p) => p !== path) : [...prev, path]
    );
  }

  function handleFileSelect(selectedFiles: FileList | null) {
    if (!selectedFiles) return;
    // Copy NOW, not inside the updater: a FileList is live, and the change handler resets
    // the input right after this call so the same file can be picked twice. React runs the
    // updater later, by which time the picker's list is already empty — which is why the
    // dialog "did nothing" while drag-and-drop (whose list nobody clears) worked.
    const picked = Array.from(selectedFiles);
    if (picked.length === 0) return;
    setFiles((prev) => [...prev, ...picked]);
  }

  function removeFile(index: number) {
    setFiles((prev) => prev.filter((_, i) => i !== index));
  }

  const destination = returnTo ?? `/p/${projectId}/board`;

  async function createTask(formData: FormData, submitToQueue: boolean) {
    setPendingIntent(submitToQueue ? "submit" : "draft");
    let createdId: string | null = null;
    try {
      // Step 1: Create the task
      const { id } = await createTaskAndReturn(formData);
      createdId = id;

      // Step 2: Upload any pending files. Keep going if one fails, but track it —
      // a silently-dropped attachment is exactly the failure mode we're fixing.
      let failedUploads = 0;
      for (const file of files) {
        try {
          await uploadAttachment(id, file);
        } catch (err) {
          failedUploads++;
          console.error(`Attachment "${file.name}" failed to upload:`, err);
        }
      }
      if (failedUploads > 0) {
        toast(
          t("taskCreateForm.toastAttachmentsFailed", { count: failedUploads }),
          "error"
        );
      }

      // Step 2b: Attach dependencies BEFORE submitting — the worker polls every few
      // seconds, so a dependency added after submission could arrive too late to hold
      // the task back.
      let failedDeps = 0;
      for (const dep of selectedDeps) {
        try {
          await addTaskDependency(id, dep.id);
        } catch (err) {
          failedDeps++;
          console.error(`Dependency on "${dep.code}" failed to add:`, err);
        }
      }
      if (failedDeps > 0) {
        toast(t("taskCreateForm.toastDependenciesFailed", { count: failedDeps }), "error");
        if (submitToQueue) {
          // Don't submit with a missing dependency — the task would start unguarded.
          // It stays as a draft; the user can fix the dependencies and submit there.
          router.push(destination);
          return;
        }
      }

      // Step 3: Optionally submit to the queue, then return to where the user came from.
      // submitTask returns { error } instead of throwing (pre-flight blockers like
      // "no AI provider connected" must reach the user verbatim). The task exists as a
      // draft either way, so redirect regardless — resubmitting the form would duplicate.
      if (submitToQueue) {
        const submitResult = await submitTask(id);
        if (submitResult?.error) {
          toast(submitResult.error, "error");
          router.push(destination);
          return;
        }
      }
      toast(
        submitToQueue
          ? t("taskCreateForm.toastSubmitted")
          : t("taskCreateForm.toastCreated")
      );
      router.push(destination);
    } catch {
      if (createdId) {
        // The task exists as a draft but submitting failed — leaving the form
        // resubmittable would create a duplicate, so redirect anyway.
        toast(t("taskCreateForm.toastSubmitFailed"), "error");
        router.push(destination);
      } else {
        toast(t("taskCreateForm.submitError"), "error");
        setPendingIntent(null);
      }
    }
  }

  return (
    <form
      action={(fd) => createTask(fd, true)}
      className="xl:grid xl:grid-cols-12 xl:items-start xl:gap-6"
    >
      <div className="space-y-5 xl:col-span-5">
      <input type="hidden" name="projectId" value={projectId} />

      <Input
        label={t("taskCreateForm.titleLabel")}
        name="title"
        placeholder={t("taskCreateForm.titlePlaceholder")}
        required
      />

      <input type="hidden" name="description" value={description} />
      <input type="hidden" name="contextFiles" value={JSON.stringify(contextFiles)} />
      <input type="hidden" name="contextDocumentIds" value={JSON.stringify(selectedDocIds)} />
      <MentionTextarea
        value={description}
        onChange={setDescription}
        contextFiles={contextFiles}
        onContextFilesChange={setContextFiles}
        projectId={projectId}
        repoId={selectedRepoId || undefined}
        label={t("taskCreateForm.descriptionLabel")}
        placeholder={t("taskCreateForm.descriptionPlaceholder")}
        showCount
        maxLength={5000}
      />

      {/* Repo selector */}
      <div className="space-y-1.5">
        <Select
          label={t("taskCreateForm.repositoryLabel")}
          name="repoId"
          required
          value={selectedRepoId}
          onChange={(e) => {
            setSelectedRepoId(e.target.value);
            // The new primary can't also be a spanned repo. The checkbox for it is filtered
            // out of the list, so leaving it in state would only skew the count in the notice.
            setSpannedRepoIds((prev) => prev.filter((id) => id !== e.target.value));
          }}
        >
          <option value="">
            {t("taskCreateForm.selectRepository")}
          </option>
          {repos.map((repo) => (
            <option key={repo.id} value={repo.id}>
              {repo.name}
            </option>
          ))}
        </Select>
        {/* Spanning repos. Hidden entirely for a single-repo project, and until a primary
            repo is chosen — "also changes" has no meaning without a "this". */}
        {selectedRepoId && repos.length > 1 && (
          <div className="pt-3">
            <label className="block text-sm font-medium text-gray-300">
              {t("taskCreateForm.spansReposLabel")}
            </label>
            <p className="mt-1 text-xs text-gray-400">
              {t("taskCreateForm.spansReposHint")}
            </p>
            <div className="mt-2 space-y-1.5">
              {repos
                .filter((repo) => repo.id !== selectedRepoId)
                .map((repo) => (
                  <label
                    key={repo.id}
                    className="flex cursor-pointer items-center gap-2 text-sm text-gray-300"
                  >
                    <input
                      type="checkbox"
                      name="spansRepoIds"
                      value={repo.id}
                      checked={spannedRepoIds.includes(repo.id)}
                      onChange={(e) =>
                        setSpannedRepoIds((prev) =>
                          e.target.checked
                            ? [...prev, repo.id]
                            : prev.filter((id) => id !== repo.id)
                        )
                      }
                      className="h-4 w-4 rounded border-white/[0.12] bg-white/[0.03] accent-primary-500"
                    />
                    <span>{repo.name}</span>
                  </label>
                ))}
            </div>
            {spannedRepoIds.length > 0 && (
              <p className="mt-2 text-xs text-amber-400/80">
                {t("taskCreateForm.spansReposNotice", {
                  count: spannedRepoIds.length + 1,
                })}
              </p>
            )}
          </div>
        )}

        {milestones.length > 0 && (
          <div className="pt-2">
            <Select
              label={t("taskMilestone.label")}
              name="milestoneId"
              value={selectedMilestoneId}
              onChange={(e) => setSelectedMilestoneId(e.target.value)}
            >
              <option value="">{t("taskMilestone.none")}</option>
              {milestones.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.title}
                </option>
              ))}
            </Select>
          </div>
        )}
        {selectedRepo && (
          <button
            type="button"
            onClick={() => setPanelOpen((o) => !o)}
            className="flex items-center gap-1.5 text-xs text-gray-400 transition-colors hover:text-gray-200 xl:hidden"
          >
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
              <path d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v8a2 2 0 01-2 2H5a2 2 0 01-2-2V7z" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            {panelOpen
              ? t("repoFilePanel.toggleHide")
              : t("repoFilePanel.toggleShow")}
          </button>
        )}
      </div>

      {/* Assign to agent (optional) — pins the task, bypassing auto-assign */}
      {assignableAgents.length > 0 && (
        <div className="space-y-1.5">
          <Select
            label={t("taskCreateForm.assignAgentLabel")}
            name="assignedAgentId"
            value={selectedAgentId}
            onChange={(e) => {
              const agentId = e.target.value;
              setSelectedAgentId(agentId);
              // Pre-select whatever this agent is configured with on the Team page — the
              // common case then needs no further decision.
              setSelectedModel(
                assignableAgents.find((a) => a.id === agentId)?.model ?? ""
              );
            }}
          >
            <option value="">{t("taskCreateForm.assignAgentAuto")}</option>
            {assignableAgents.map((agent) => (
              <option key={agent.id} value={agent.id}>
                {agent.name} — {labels.agentRole(agent.role)}
              </option>
            ))}
          </Select>
          <p className="text-xs text-gray-400">{t("taskCreateForm.assignAgentHint")}</p>
        </div>
      )}

      {/* Model for this task — defaults to the selected agent's Team page model */}
      <div className="space-y-1.5">
        <input type="hidden" name="model" value={modelOverride} />
        <Select
          label={t("taskCreateForm.modelLabel")}
          value={selectedModel}
          onChange={(e) => setSelectedModel(e.target.value)}
        >
          <option value="">{t("taskCreateForm.modelAgentDefault")}</option>
          {modelGroups.map(([provider, options]) => {
            const items = options.map((m) => {
              // Annotated rows carry a readiness verdict and say so in the label; a model nobody
              // could run right now stays visible (so the list is honest about what exists)
              // but can't be picked. A model waiting for a sign-in stays pickable — the task
              // waits on the runner. Rows without a verdict keep their connected wording.
              const unavailable = m.billing === "unavailable";
              const label =
                m.billing === "runner" && m.reason !== "runnerWaitingSignIn"
                  ? t("modelBilling.optionRunner", { model: m.name })
                  : unavailable || m.connected === false || m.reason === "runnerWaitingSignIn"
                    ? t("taskCreateForm.modelNotConnected", { model: m.name })
                    : m.name;
              return (
                <option key={m.id} value={m.id} disabled={unavailable}>
                  {label}
                </option>
              );
            });
            return provider ? (
              <optgroup key={provider} label={providerLabel(provider)}>
                {items}
              </optgroup>
            ) : (
              <Fragment key="ungrouped">{items}</Fragment>
            );
          })}
        </Select>
        {/* Directly under the picker, before the hint: "can my runner run this" is the answer
            to the question the picker just asked. */}
        {billingNote && (
          <ModelBillingNote
            billing={billingNote.billing}
            reason={billingNote.reason}
            provider={billingNote.provider}
            model={billingNote.model}
          />
        )}
        <TaskForecastSummary forecast={forecast} />
        <p className="text-xs text-gray-400">{t("taskCreateForm.modelHint")}</p>
      </div>

      {/* Skip planning — absent from the FormData when unchecked, so the parser reads "true" */}
      <div className="space-y-1.5">
        <label
          className={cn(
            "flex cursor-pointer items-start gap-3 rounded-xl px-4 py-3",
            "border border-white/[0.06] bg-white/[0.03]",
            "transition-colors duration-200 hover:bg-white/[0.05]"
          )}
        >
          <input
            type="checkbox"
            name="skipPlanning"
            value="true"
            className="mt-0.5 h-4 w-4 shrink-0 rounded border-white/[0.1] bg-white/[0.04] accent-primary-500"
          />
          <span className="space-y-1">
            <span className="block text-sm font-medium text-gray-300">
              {t("taskCreateForm.skipPlanningLabel")}
            </span>
            <span className="block text-xs text-gray-400">
              {t("taskCreateForm.skipPlanningHint")}
            </span>
          </span>
        </label>
      </div>

      {/* Task Type */}
      <div className="space-y-1.5">
        <label className="block text-sm font-medium text-gray-300">
          {t("taskCreateForm.taskTypeLabel")}
        </label>
        <div className="grid grid-cols-4 gap-2">
          {TASK_TYPES.map((t) => (
            <label
              key={t.key}
              className="group relative cursor-pointer"
            >
              <input
                type="radio"
                name="type"
                value={t.key}
                defaultChecked={t.key === "Feature"}
                className="peer sr-only"
              />
              <div
                className={cn(
                  "flex items-center justify-center gap-1.5 rounded-xl border border-white/[0.06] px-3 py-2.5 text-xs font-medium text-gray-400",
                  "transition-all duration-200",
                  "peer-checked:border-transparent peer-checked:text-white peer-checked:bg-white/[0.06]",
                  "hover:bg-white/[0.04]"
                )}
              >
                <TaskTypeIconSmall type={t.icon} />
                {labels.taskType(t.key)}
              </div>
            </label>
          ))}
        </div>
      </div>

      {/* Priority */}
      <div className="space-y-1.5">
        <label className="block text-sm font-medium text-gray-300">
          {t("taskCreateForm.priorityLabel")}
        </label>
        <div className="grid grid-cols-4 gap-2">
          {PRIORITIES.map((p) => (
            <label
              key={p.key}
              className="group relative cursor-pointer"
            >
              <input
                type="radio"
                name="priority"
                value={p.key}
                defaultChecked={p.key === "medium"}
                className="peer sr-only"
              />
              <div
                className={cn(
                  "flex items-center justify-center gap-1.5 rounded-xl border border-white/[0.06] px-3 py-2.5 text-xs font-medium text-gray-400",
                  "transition-all duration-200",
                  "peer-checked:border-transparent peer-checked:text-white",
                  "hover:bg-white/[0.04]"
                )}
              >
                <span
                  className="h-1.5 w-1.5 rounded-full shrink-0"
                  style={{ backgroundColor: p.color }}
                />
                {labels.priority(p.key)}
              </div>
              <div
                className="pointer-events-none absolute inset-0 rounded-xl opacity-0 peer-checked:opacity-100 transition-opacity"
                style={{ backgroundColor: `${p.color}10` }}
              />
            </label>
          ))}
        </div>
      </div>

      {/* Tags */}
      <Input
        label={t("taskCreateForm.tagsLabel")}
        name="tags"
        placeholder={t("taskCreateForm.tagsPlaceholder")}
        hint={t("taskCreateForm.tagsHint")}
      />

      {/* Depends on — attached before submission so the queue never picks this task
          up ahead of the tasks it builds on */}
      <div className="space-y-1.5">
        <label className="block text-sm font-medium text-gray-300">
          {t("taskCreateForm.dependsOnLabel")}
        </label>

        {selectedDeps.length > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {selectedDeps.map((dep) => (
              <span
                key={dep.id}
                className="inline-flex items-center gap-1.5 rounded-lg border border-amber-500/25 bg-amber-500/10 px-2 py-1"
              >
                <span className="text-xs text-amber-400/80">{dep.code}</span>
                <span className="max-w-[180px] truncate text-xs text-gray-300">{dep.title}</span>
                <button
                  type="button"
                  aria-label={t("taskCreateForm.dependsOnRemove")}
                  onClick={() =>
                    setSelectedDeps((prev) => prev.filter((d) => d.id !== dep.id))
                  }
                  className="rounded p-0.5 text-gray-400 transition-colors hover:bg-red-500/10 hover:text-red-400"
                >
                  <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
                    <path d="M12 4L4 12M4 4l8 8" strokeLinecap="round" />
                  </svg>
                </button>
              </span>
            ))}
          </div>
        )}

        <button
          type="button"
          onClick={() => setDepPickerOpen((v) => !v)}
          className={cn(
            "flex items-center gap-1.5 rounded-lg px-2 py-1 text-xs transition-colors",
            depPickerOpen
              ? "bg-white/[0.08] text-white"
              : "text-gray-400 hover:bg-white/[0.04] hover:text-gray-200"
          )}
        >
          {depPickerOpen ? (
            t("taskCreateForm.dependsOnDone")
          ) : (
            <>
              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
              </svg>
              {t("taskCreateForm.dependsOnAdd")}
            </>
          )}
        </button>

        {depPickerOpen && (
          <div className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-2">
            <input
              value={depFilter}
              onChange={(e) => setDepFilter(e.target.value)}
              placeholder={t("taskCreateForm.dependsOnFilterPlaceholder")}
              className={cn(
                "mb-2 w-full rounded-lg border border-white/[0.08] bg-white/[0.03] px-2.5 py-1.5",
                "text-xs text-gray-200 placeholder:text-gray-500",
                "focus:border-primary-500/50 focus:outline-none"
              )}
            />
            <div className="max-h-56 space-y-0.5 overflow-y-auto">
              {depCandidates === null ? (
                <p className="px-2 py-3 text-center text-xs text-gray-400">
                  {t("taskCreateForm.dependsOnLoading")}
                </p>
              ) : visibleDepCandidates.length === 0 ? (
                <p className="px-2 py-3 text-center text-xs text-gray-400">
                  {t("taskCreateForm.dependsOnNoMatches")}
                </p>
              ) : (
                visibleDepCandidates.map((c) => (
                  <button
                    key={c.id}
                    type="button"
                    onClick={() => setSelectedDeps((prev) => [...prev, c])}
                    className="flex w-full items-center gap-2 rounded-lg px-1.5 py-1.5 text-left transition-colors hover:bg-white/[0.04]"
                  >
                    <span className="text-xs text-gray-400">{c.code}</span>
                    <span className="truncate text-xs text-gray-300">{c.title}</span>
                  </button>
                ))
              )}
            </div>
          </div>
        )}

        <p className="text-xs text-gray-400">{t("taskCreateForm.dependsOnHint")}</p>
      </div>

      {/* Context documents — injected into agent prompts when the task runs */}
      <div className="space-y-1.5">
        <label className="block text-sm font-medium text-gray-300">
          {t("taskDocuments.title")}
        </label>

        {selectedDocIds.length > 0 && (
          <div className="space-y-1">
            {selectedDocIds.map((id) => {
              const doc = docById.get(id);
              return (
                <div
                  key={id}
                  className="group flex items-center gap-2 rounded-lg bg-white/[0.03] px-2 py-1.5"
                >
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" className="shrink-0 text-amber-400/80">
                    <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
                    <path d="M14 2v6h6M16 13H8M16 17H8" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-xs text-gray-300">
                      {doc?.title ?? t("taskDocuments.unknownDocument")}
                    </span>
                    {doc?.path && (
                      <span className="block truncate font-mono text-xs text-gray-400">
                        {doc.path}
                      </span>
                    )}
                  </span>
                  <button
                    type="button"
                    onClick={() => toggleDocument(id)}
                    aria-label={t("taskDocuments.remove")}
                    className="shrink-0 rounded p-1 text-gray-400 transition-colors hover:bg-red-500/10 hover:text-red-400"
                  >
                    <svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
                      <path d="M12 4L4 12M4 4l8 8" strokeLinecap="round" />
                    </svg>
                  </button>
                </div>
              );
            })}
          </div>
        )}

        <button
          type="button"
          onClick={() => setDocPickerOpen((v) => !v)}
          className={cn(
            "flex items-center gap-1.5 rounded-lg px-2 py-1 text-xs transition-colors",
            docPickerOpen
              ? "bg-white/[0.08] text-white"
              : "text-gray-400 hover:bg-white/[0.04] hover:text-gray-200"
          )}
        >
          {docPickerOpen ? (
            t("taskDocuments.done")
          ) : (
            <>
              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
              </svg>
              {t("taskDocuments.attachDocuments")}
            </>
          )}
        </button>

        {docPickerOpen && (
          <div className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-2">
            <input
              value={docFilter}
              onChange={(e) => setDocFilter(e.target.value)}
              placeholder={t("taskDocuments.filterPlaceholder")}
              className={cn(
                "mb-2 w-full rounded-lg border border-white/[0.08] bg-white/[0.03] px-2.5 py-1.5",
                "text-xs text-gray-200 placeholder:text-gray-500",
                "focus:border-primary-500/50 focus:outline-none"
              )}
            />
            <div className="max-h-56 space-y-1.5 overflow-y-auto">
              {documents === null ? (
                <p className="px-2 py-3 text-center text-xs text-gray-400">
                  {t("taskDocuments.loading")}
                </p>
              ) : documents.length === 0 ? (
                <p className="px-2 py-3 text-center text-xs text-gray-400">
                  {t("taskDocuments.noDocuments")}
                </p>
              ) : groupedDocs.length === 0 ? (
                <p className="px-2 py-3 text-center text-xs text-gray-400">
                  {t("taskDocuments.noMatches")}
                </p>
              ) : (
                groupedDocs.map(([folder, docs]) => (
                  <div key={folder}>
                    <p className="px-1 py-0.5 text-label">
                      {folder}
                    </p>
                    {docs.map((doc) => {
                      const checked = selectedDocIds.includes(doc.id);
                      return (
                        <button
                          key={doc.id}
                          type="button"
                          onClick={() => toggleDocument(doc.id)}
                          className="flex w-full items-center gap-2 rounded-lg px-1.5 py-1.5 text-left transition-colors hover:bg-white/[0.04]"
                        >
                          <span
                            className={cn(
                              "flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded border",
                              checked
                                ? "border-amber-500/60 bg-amber-500/20 text-amber-400"
                                : "border-white/[0.12]"
                            )}
                          >
                            {checked && (
                              <svg width="9" height="9" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3">
                                <path d="M5 13l4 4L19 7" strokeLinecap="round" strokeLinejoin="round" />
                              </svg>
                            )}
                          </span>
                          <span className="truncate text-xs text-gray-300">{doc.title}</span>
                        </button>
                      );
                    })}
                  </div>
                ))
              )}
            </div>
          </div>
        )}

        <p className="text-xs text-gray-400">{t("taskDocuments.hint")}</p>
      </div>

      {/* Attachments */}
      <div className="space-y-1.5">
        <label className="block text-sm font-medium text-gray-300">
          {t("taskCreateForm.attachmentsLabel")}
        </label>
        <div
          className={cn(
            "flex flex-col items-center justify-center rounded-xl border-2 border-dashed border-white/[0.06] p-6",
            "transition-colors hover:border-white/[0.12] hover:bg-white/[0.02]",
            "cursor-pointer"
          )}
          onClick={() => fileInputRef.current?.click()}
          onDragOver={(e) => {
            e.preventDefault();
            e.currentTarget.classList.add("border-primary-500/30", "bg-white/[0.03]");
          }}
          onDragLeave={(e) => {
            e.currentTarget.classList.remove("border-primary-500/30", "bg-white/[0.03]");
          }}
          onDrop={(e) => {
            e.preventDefault();
            e.currentTarget.classList.remove("border-primary-500/30", "bg-white/[0.03]");
            handleFileSelect(e.dataTransfer.files);
          }}
        >
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" className="text-gray-400 mb-2">
            <path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4M17 8l-5-5-5 5M12 3v12" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <span className="text-xs text-gray-400">
            {t("taskCreateForm.dropFiles")}
          </span>
        </div>
        <input
          ref={fileInputRef}
          type="file"
          multiple
          className="hidden"
          onChange={(e) => {
            handleFileSelect(e.target.files);
            e.target.value = "";
          }}
        />

        {/* File previews */}
        {files.length > 0 && (
          <div className="mt-2 space-y-1.5">
            {files.map((file, i) => (
              <div
                key={i}
                className="flex items-center gap-2 rounded-lg bg-white/[0.03] px-3 py-2"
              >
                {file.type.startsWith("image/") ? (
                  <img
                    src={URL.createObjectURL(file)}
                    alt={file.name}
                    className="h-8 w-8 rounded object-cover"
                  />
                ) : (
                  <div className="flex h-8 w-8 items-center justify-center rounded bg-white/[0.04]">
                    <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" className="text-gray-400">
                      <path d="M9 1H4a1 1 0 00-1 1v12a1 1 0 001 1h8a1 1 0 001-1V5l-4-4z" strokeLinejoin="round" />
                    </svg>
                  </div>
                )}
                <span className="flex-1 text-xs text-gray-300 truncate">
                  {file.name}
                </span>
                <span className="text-xs text-gray-400 shrink-0">
                  {(file.size / 1024).toFixed(0)} KB
                </span>
                <button
                  type="button"
                  onClick={() => removeFile(i)}
                  className="shrink-0 rounded p-1 text-gray-400 hover:text-red-400 hover:bg-red-500/10 transition-colors"
                >
                  <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                    <path d="M4.5 4.5l7 7M11.5 4.5l-7 7" strokeLinecap="round" />
                  </svg>
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="flex flex-col gap-2 sm:flex-row">
        <Button
          type="submit"
          size="lg"
          disabled={pendingIntent !== null}
          loading={pendingIntent === "submit"}
          className="w-full sm:flex-1"
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M14 2L7 9M14 2l-5 14-2-6-6-2 14-5z" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {t("taskCreateForm.createAndSubmit")}
        </Button>
        <Button
          type="submit"
          variant="ghost"
          size="lg"
          formAction={(fd: FormData) => createTask(fd, false)}
          disabled={pendingIntent !== null}
          loading={pendingIntent === "draft"}
          className="w-full sm:w-auto"
        >
          {t("taskCreateForm.saveAsDraft")}
        </Button>
      </div>
      </div>

      {/* Repo file browser — always visible at xl:, toggled below. */}
      <div
        className={cn(
          panelOpen ? "block" : "hidden",
          "mt-6 xl:col-span-7 xl:sticky xl:top-6 xl:mt-0 xl:block"
        )}
      >
        <RepoFilePickerPanel
          key={selectedRepoId || "none"}
          projectId={projectId}
          repo={selectedRepo}
          linkedFiles={contextFiles}
          onToggleLink={toggleContextFile}
        />
      </div>
    </form>
  );
}

function TaskTypeIconSmall({ type }: { type: string }) {
  const cls = "w-3.5 h-3.5";
  switch (type) {
    case "bug":
      return (
        <svg className={cn(cls, "text-red-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <circle cx="8" cy="8" r="3" />
          <path d="M8 1v4M8 11v4M1 8h4M11 8h4" strokeLinecap="round" />
        </svg>
      );
    case "arrows":
      return (
        <svg className={cn(cls, "text-cyan-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M2 8h5l-2-3M14 8H9l2 3" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
    case "sparkle":
      return (
        <svg className={cn(cls, "text-amber-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M8 2l1.5 3 3.5.5-2.5 2.5.5 3.5L8 10l-3 1.5.5-3.5L3 5.5 6.5 5z" strokeLinejoin="round" />
        </svg>
      );
    default: // lightbulb
      return (
        <svg className={cn(cls, "text-violet-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M8 2a4 4 0 014 4c0 1.5-.8 2.5-1.5 3.5S9 11 9 12H7c0-1-.8-2-1.5-3S4 7.5 4 6a4 4 0 014-4z" />
          <path d="M6.5 14h3" strokeLinecap="round" />
        </svg>
      );
  }
}

export { TaskCreateForm };
