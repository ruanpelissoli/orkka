import { Fragment, useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button, Input, Select, Textarea, Badge } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { AgentSkillsPicker } from "@/components/skills";
import { AGENT_ROLE_MAP, DEV_AGENT_ROLE_KEYS, providerLabel } from "@/lib/constants";
import {
  formatUsdEstimate,
  formatTokenCount,
  formatRelativeTime,
  groupModelsByProvider,
  providerFromModelId,
} from "@/lib/utils";
import { updateAgent, deleteAgent } from "@/lib/actions";
import type { AiModel, ProjectAgent, ProjectRepo } from "@/lib/types";
import { useLabels } from "@/hooks/use-labels";
import { useSelectableModels } from "@/hooks/use-selectable-models";
import { billingForModel } from "@/lib/model-billing";
import type { AnnotatedModel } from "@/lib/model-billing";
import { ModelBillingNote } from "@/components/task/ModelBillingNote";

interface AgentConfigTabProps {
  agent: ProjectAgent;
  repos: ProjectRepo[];
  /** Enabled chat models from the catalog, runner-only rows included — the runner-aware hook
   * decides per row whether it is pickable. Empty = the catalog is unavailable. */
  models: AiModel[];
  projectId: string;
  onClose: () => void;
}

function AgentConfigTab({
  agent,
  repos,
  models,
  projectId,
  onClose,
}: AgentConfigTabProps) {
  const t = useTranslations("dashboard");
  const tc = useTranslations("common");
  const [isPending, startTransition] = useTransition();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [showSystemPrompt, setShowSystemPrompt] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const roleInfo = AGENT_ROLE_MAP[agent.role];

  const labels = useLabels();

  // Which of these models the user's runner can run now, which wait for a sign-in, and which
  // can't run at all right now. Null until the runner status is known, in which case the plain
  // catalog renders without readiness labels.
  const { models: annotated, coverage } = useSelectableModels(models, agent.model);
  const [selectedModel, setSelectedModel] = useState(agent.model);
  const annotatedOf = new Map<string, AnnotatedModel>((annotated ?? []).map((m) => [m.id, m]));
  const catalogRows: AiModel[] = annotated ?? models;

  // A model the catalog no longer offers (retired, or disabled by a platform admin) must stay
  // listed — otherwise the browser selects the first option and saving would silently move the
  // agent onto a different model. Not disabled, for the same reason.
  const modelOptions: AiModel[] = catalogRows.some((m) => m.id === agent.model)
    ? catalogRows
    : [
        ...catalogRows,
        {
          id: agent.model,
          name: t("agentConfig.noLongerAvailable", { name: agent.model }),
          provider: "",
          kind: "Chat" as const,
          description: "",
          supportsMcp: true,
          isEnabled: false,
          requiresRunner: false,
          sortOrder: -1,
        },
      ];
  const modelGroups = groupModelsByProvider(modelOptions);

  // The note under the picker follows the CURRENT selection, not the saved one — the point is
  // to say "this will wait for a sign-in" before Save, not after.
  const billingNote =
    annotated && selectedModel
      ? {
          ...billingForModel(selectedModel, annotated, coverage),
          provider:
            annotated.find((m) => m.id === selectedModel)?.provider ??
            providerFromModelId(selectedModel),
        }
      : null;

  function handleSave(formData: FormData) {
    setError(null);
    startTransition(async () => {
      try {
        await updateAgent(projectId, agent.id, formData);
        onClose();
      } catch (e) {
        setError(e instanceof Error ? e.message : t("agentConfig.updateError"));
      }
    });
  }

  function handleDelete() {
    if (!confirmDelete) {
      setConfirmDelete(true);
      setTimeout(() => setConfirmDelete(false), 3000);
      return;
    }
    startTransition(async () => {
      try {
        await deleteAgent(projectId, agent.id);
        onClose();
      } catch (e) {
        setError(e instanceof Error ? e.message : t("agentConfig.deleteError"));
      }
    });
  }

  return (
    <div className="space-y-6">
      {/* Edit Form */}
      <form action={handleSave} className="space-y-4">
        <Input
          label={t("agentConfig.nameLabel")}
          name="name"
          defaultValue={agent.name}
        />

        <div>
          <label className="mb-1.5 block text-sm font-medium text-gray-300">
            {t("agentConfig.modelLabel")}
          </label>
          {/* No catalog (API unreachable) renders no select at all, so the FormData carries no
              "model" and the updateAgent action leaves the agent's model unchanged. */}
          {models.length === 0 ? (
            <p className="rounded-xl border border-amber-500/20 bg-amber-500/[0.06] px-3.5 py-2.5 text-xs text-amber-200/90">
              {t("agentConfig.catalogUnavailable", { model: agent.model })}
            </p>
          ) : (
            <Select
              name="model"
              defaultValue={agent.model}
              onChange={(e) => setSelectedModel(e.target.value)}
            >
              {modelGroups.map(([provider, groupModels]) => {
                const items = groupModels.map((m) => {
                  const blocked = agent.role === "QA" && !m.supportsMcp;
                  const billing = annotatedOf.get(m.id)?.billing;
                  const waiting = annotatedOf.get(m.id)?.reason === "runnerWaitingSignIn";
                  // A model nobody could run right now stays visible but can't be picked —
                  // except the agent's own saved one, which stays selectable so a save never
                  // silently rewrites it.
                  const unavailable = billing === "unavailable" && m.id !== agent.model;
                  const label = blocked
                    ? t("agentConfig.noBrowserTools", { name: m.name })
                    : billing === "runner" && !waiting
                      ? t("modelBilling.optionRunner", { model: m.name })
                      : billing === "unavailable" || waiting
                        ? t("taskCreateForm.modelNotConnected", { model: m.name })
                        : m.name;
                  return (
                    <option key={m.id} value={m.id} disabled={blocked || unavailable}>
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
          )}
          {models.length > 0 && billingNote && (
            <ModelBillingNote
              billing={billingNote.billing}
              reason={billingNote.reason}
              provider={billingNote.provider}
              model={selectedModel}
              className="mt-1.5"
            />
          )}
          {agent.role === "QA" && (
            <p className="mt-1 text-xs text-gray-400">
              {t("agentConfig.qaModelHint")}
            </p>
          )}
        </div>

        {/* Repo scope (dev roles only) — tasks for a checked repo are routed to this
            agent first; nothing checked = the agent works across all repos. */}
        {DEV_AGENT_ROLE_KEYS.has(agent.role) && repos.length > 0 && (
          <div>
            <label className="mb-1.5 block text-sm font-medium text-gray-300">
              {t("agentConfig.repositoriesLabel")}
            </label>
            {/* Marker: lets the updateAgent action tell "none checked" (all repos) apart
                from "group not rendered" (leave scope unchanged). */}
            <input type="hidden" name="repoIdsPresent" value="1" />
            <div className="space-y-1.5 rounded-xl border border-white/[0.08] bg-white/[0.03] p-3">
              {repos.map((repo) => (
                <label key={repo.id} className="flex cursor-pointer items-center gap-2.5">
                  <input
                    type="checkbox"
                    name="repoIds"
                    value={repo.id}
                    defaultChecked={agent.repoIds.includes(repo.id)}
                    className="h-4 w-4 rounded border-white/20 bg-white/[0.06] accent-primary-500"
                  />
                  <span className="text-sm text-gray-200">{repo.name}</span>
                </label>
              ))}
            </div>
            <p className="mt-1 text-xs text-gray-400">
              {t("agentConfig.repositoriesHint")}
            </p>
          </div>
        )}

        {/* Skills (project playbooks) — checkbox group with its own presence marker, same
            contract as the repo scope above. */}
        <AgentSkillsPicker projectId={projectId} skillIds={agent.skillIds ?? []} />

        <div>
          <Textarea
            label={t("agentConfig.customInstructionsLabel")}
            name="userInstructions"
            defaultValue={agent.userInstructions}
            maxLength={4000}
            showCount
          />
          <p className="mt-1 text-xs text-gray-400">
            {t("agentConfig.customInstructionsHint")}
          </p>
        </div>

        {error && (
          <p className="text-xs text-red-400">{error}</p>
        )}

        <RequirePermission permission="AgentsConfigure">
          <Button type="submit" size="sm" disabled={isPending}>
            {isPending ? tc("actions.saving") : t("agentConfig.saveChanges")}
          </Button>
        </RequirePermission>
      </form>

      {/* Stats */}
      <div className="border-t border-white/[0.04] pt-4">
        <h3 className="mb-3 text-label">
          {t("agentConfig.statistics")}
        </h3>
        <div className="grid grid-cols-2 gap-3">
          <StatBox
            label={t("agentConfig.tokensIn")}
            value={formatTokenCount(agent.billed?.inputTokens ?? agent.totalTokensIn)}
          />
          <StatBox
            label={t("agentConfig.tokensOut")}
            value={formatTokenCount(agent.billed?.outputTokens ?? agent.totalTokensOut)}
          />
          {/* Cache is shown only when the usage rows have the split. The stored counters have no
              bucket for it, so rendering zeros on an unbilled host would be a false claim. */}
          {agent.billed && (
            <>
              <StatBox
                label={t("agentConfig.cacheRead")}
                value={formatTokenCount(agent.billed.cacheReadTokens)}
              />
              <StatBox
                label={t("agentConfig.cacheWrite")}
                value={formatTokenCount(agent.billed.cacheWriteTokens)}
              />
            </>
          )}
          <StatBox
            label={t("agentConfig.tasksCompleted")}
            value={String(agent.tasksCompleted)}
            valueClass="text-emerald-400"
          />
          <StatBox
            label={t("agentConfig.tasksFailed")}
            value={String(agent.tasksFailed)}
            valueClass={agent.tasksFailed > 0 ? "text-red-400" : undefined}
          />
          <StatBox
            label={t("agentConfig.estimatedCost")}
            value={formatUsdEstimate(agent.totalCostUsd)}
          />
          <StatBox label={t("agentConfig.activeSince")} value={formatRelativeTime(agent.createdAt)} />
        </div>
      </div>

      {/* System Prompt */}
      <div className="border-t border-white/[0.04] pt-4">
        <button
          type="button"
          onClick={() => setShowSystemPrompt(!showSystemPrompt)}
          className="flex items-center gap-2 text-xs font-medium text-gray-400 transition-colors hover:text-gray-300"
        >
          <svg
            width="12"
            height="12"
            viewBox="0 0 12 12"
            fill="none"
            className={`transition-transform ${showSystemPrompt ? "rotate-90" : ""}`}
          >
            <path d="M4 2l4 4-4 4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {t("agentConfig.viewSystemPrompt")}
        </button>
        {showSystemPrompt && (
          <pre className="mt-3 max-h-60 overflow-auto rounded-xl bg-black/30 p-4 font-mono text-xs text-gray-400 leading-relaxed">
            {agent.systemPrompt}
          </pre>
        )}
      </div>

      {/* Role & Danger Zone */}
      <div className="flex items-center justify-between border-t border-white/[0.04] pt-4">
        <Badge
          size="md"
          dot
          dotColor={roleInfo?.color}
          style={roleInfo ? { borderColor: `${roleInfo.color}33`, color: roleInfo.color, backgroundColor: `${roleInfo.color}1a` } : undefined}
        >
          {labels.agentRole(agent.role)}
        </Badge>
        {agent.isDefault ? (
          <span className="text-xs text-gray-400">{t("agentConfig.defaultAgent")}</span>
        ) : (
          <RequirePermission permission="AgentsConfigure">
            <Button
              variant="danger"
              size="sm"
              onClick={handleDelete}
              disabled={isPending}
            >
              {confirmDelete ? t("agentConfig.confirmDelete") : t("agentConfig.deleteAgent")}
            </Button>
          </RequirePermission>
        )}
      </div>
    </div>
  );
}

function StatBox({
  label,
  value,
  valueClass,
}: {
  label: string;
  value: string;
  valueClass?: string;
}) {
  return (
    <div className="rounded-xl bg-white/[0.02] border border-white/[0.04] p-3">
      <p className="text-label">{label}</p>
      <p className={`mt-0.5 font-mono text-sm font-medium ${valueClass ?? "text-gray-300"}`}>
        {value}
      </p>
    </div>
  );
}

export { AgentConfigTab };
