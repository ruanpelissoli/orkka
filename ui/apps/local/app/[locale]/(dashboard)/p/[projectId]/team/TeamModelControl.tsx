import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button, ConfirmDialog, Select, useToast } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { providerLabel } from "@/lib/constants";
import { groupModelsByProvider } from "@/lib/utils";
import { setAgentsModel } from "@/lib/actions";
import { useSelectableModels } from "@/hooks/use-selectable-models";
import type { AiModel, ProjectAgent } from "@/lib/types";

interface TeamModelControlProps {
  agents: ProjectAgent[];
  /** Enabled chat models from the catalog — the same list the per-agent picker gets. */
  models: AiModel[];
  projectId: string;
}

/**
 * "Set every agent's model": one select + Apply (with a confirm) that pins a model on the
 * whole team via PUT /api/projects/{id}/agents/model — e.g. Copilot Auto for its discounted
 * premium requests. Lists only models the runner has READY (the per-agent picker's source,
 * filtered to `connected`), so a bulk change never parks the team waiting for a sign-in.
 * Agents' saved models are never changed silently: only this explicit action moves them.
 */
function TeamModelControl({ agents, models, projectId }: TeamModelControlProps) {
  const t = useTranslations("dashboard.teamModel");
  const tc = useTranslations("common.actions");
  const { toast } = useToast();
  const [isPending, startTransition] = useTransition();
  const [confirming, setConfirming] = useState(false);
  const [selected, setSelected] = useState("");

  const { models: annotated } = useSelectableModels(models);
  // Null while the runner status is unknown — render nothing rather than flash "none ready".
  if (annotated === null || agents.length === 0) return null;

  const ready = annotated.filter((m) => m.connected);
  const groups = groupModelsByProvider(ready);
  const chosen = ready.find((m) => m.id === selected) ?? ready[0];
  const qaSkipped = chosen ? !chosen.supportsMcp && agents.some((a) => a.role === "QA") : false;

  function apply() {
    if (!chosen) return;
    const model = chosen;
    startTransition(async () => {
      try {
        const result = await setAgentsModel(projectId, model.id);
        setConfirming(false);
        const parts = [t("done", { count: result.updatedCount, model: model.name })];
        if (result.skippedQaCount > 0)
          parts.push(t("skippedQa", { count: result.skippedQaCount }));
        toast(parts.join(" "), "success");
      } catch (e) {
        setConfirming(false);
        toast(e instanceof Error && e.message ? e.message : t("error"), "error");
      }
    });
  }

  return (
    <RequirePermission permission="AgentsConfigure">
      <section
        data-testid="team-model-control"
        className="mb-6 rounded-xl border border-white/[0.06] bg-white/[0.02] p-4"
      >
        <h2 className="text-sm font-semibold text-gray-200">{t("title")}</h2>
        <p className="mt-1 text-xs text-gray-400">{t("hint")}</p>
        {ready.length === 0 ? (
          <p className="mt-3 text-xs text-amber-200/90">{t("noReadyModels")}</p>
        ) : (
          <div className="mt-3 flex flex-col gap-2 sm:flex-row sm:items-center">
            <div className="sm:w-80">
              <Select
                aria-label={t("modelLabel")}
                name="teamModel"
                value={chosen?.id ?? ""}
                onChange={(e) => setSelected(e.target.value)}
                disabled={isPending}
              >
                {groups.map(([provider, items]) => (
                  <optgroup key={provider} label={providerLabel(provider)}>
                    {items.map((m) => (
                      <option key={m.id} value={m.id}>
                        {m.name}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </Select>
            </div>
            <Button
              type="button"
              size="sm"
              onClick={() => setConfirming(true)}
              disabled={isPending || !chosen}
            >
              {t("apply")}
            </Button>
          </div>
        )}
        <ConfirmDialog
          open={confirming}
          title={t("confirmTitle")}
          message={
            <>
              <p>{t("confirmMessage", { model: chosen?.name ?? "" })}</p>
              {qaSkipped && (
                <p className="mt-2">{t("confirmQaNote", { model: chosen?.name ?? "" })}</p>
              )}
            </>
          }
          confirmLabel={t("confirm")}
          cancelLabel={tc("cancel")}
          pending={isPending}
          onConfirm={apply}
          onCancel={() => setConfirming(false)}
        />
      </section>
    </RequirePermission>
  );
}

export { TeamModelControl };
