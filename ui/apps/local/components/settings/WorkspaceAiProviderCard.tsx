import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, ConfirmDialog, GlassCard, useToast } from "@/components/ui";
import { PageError } from "@/components/page";
import { signInHint } from "@/components/runner/ProviderSignInList";
import { useApiQuery } from "@/hooks/use-api-query";
import { api } from "@/lib/api";
import { setWorkspaceAiProvider } from "@/lib/actions";
import { cliProviderLabel, isProviderReady } from "@/lib/model-billing";
import { cn } from "@/lib/utils";
import type {
  MyRunnerStatus,
  RunnerProviderStatus,
  WorkspaceAiProviderChoice,
} from "@/lib/types";

type Readiness = "ready" | "notSignedIn" | "notInstalled" | "unknown";

/** The runner's rows: live while online, the last report while offline, null when never seen. */
function providerRows(status: MyRunnerStatus | null): RunnerProviderStatus[] | null {
  if (!status) return null;
  return status.online ? (status.providers ?? []) : status.lastKnownProviders;
}

function readinessOf(rows: RunnerProviderStatus[] | null, cli: string): Readiness {
  if (!rows) return "unknown";
  const row = rows.find((p) => p.provider === cli);
  if (!row || !row.cliFound) return "notInstalled";
  return isProviderReady(row) ? "ready" : "notSignedIn";
}

/** The command that signs this CLI in: the Machine page's hint for the guarded three, the
 * CLI itself (its own sign-in prompt) for the rest. */
function fixCommand(rows: RunnerProviderStatus[] | null, cli: string): string {
  const row = rows?.find((p) => p.provider === cli);
  return signInHint(cli, row?.authMode, row?.authDetail)?.command ?? cli;
}

/**
 * Settings → Workspace → AI provider: which vendor's CLI this workspace's teams run on. "My
 * company pays for Copilot" is one choice here; every new project's agents then get Copilot
 * Auto, and changing it can switch the existing projects too (the API reports the counts).
 * Each provider shows its readiness on this machine and, when it isn't signed in, the command
 * that fixes it.
 */
export function WorkspaceAiProviderCard() {
  const t = useTranslations("dashboard.workspaceSettings.aiProvider");
  const tc = useTranslations("common.actions");
  const { toast } = useToast();
  const [isPending, startTransition] = useTransition();
  const [selected, setSelected] = useState<WorkspaceAiProviderChoice | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [applyExisting, setApplyExisting] = useState(true);

  const { data, error, reload } = useApiQuery(async () => {
    const [setting, runner] = await Promise.all([
      api.org.aiProvider(""),
      // The readiness rows are a hint: the setting still loads with the runner unknown.
      api.runner.status("").catch(() => null),
    ]);
    return { setting, runner };
  }, []);

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return null;

  const { setting, runner } = data;
  const rows = providerRows(runner);
  const choice = selected ?? setting.provider;
  const dirty = choice !== setting.provider;
  const label = (key: string) => (key === "automatic" ? t("automatic") : cliProviderLabel(key));

  function save() {
    startTransition(async () => {
      try {
        const result = await setWorkspaceAiProvider(choice, applyExisting);
        setConfirming(false);
        setSelected(null);
        const name = label(result.provider);
        const parts = [
          result.applied
            ? t("savedApplied", {
                provider: name,
                count: result.updatedCount,
                projects: result.projectCount,
              })
            : t("saved", { provider: name }),
        ];
        if (result.applied && result.skippedQaCount > 0)
          parts.push(t("savedQa", { count: result.skippedQaCount }));
        toast(parts.join(" "), "success");
        reload();
      } catch (e) {
        setConfirming(false);
        toast(e instanceof Error && e.message ? e.message : t("error"), "error");
      }
    });
  }

  return (
    <GlassCard padding="md" data-testid="workspace-ai-provider">
      <h3 className="font-display text-sm font-semibold text-white">{t("title")}</h3>
      <p className="mt-1 max-w-xl text-xs text-gray-400">{t("body")}</p>

      <div role="radiogroup" aria-label={t("title")} className="mt-3 space-y-2">
        {setting.choices.map((key) => {
          const readiness = key === "automatic" ? null : readinessOf(rows, key);
          const checked = key === choice;
          return (
            <label
              key={key}
              data-testid={`ai-provider-${key}`}
              className={cn(
                "flex cursor-pointer items-start gap-3 rounded-xl border p-3",
                checked
                  ? "border-primary-500/40 bg-primary-500/[0.06]"
                  : "border-white/[0.06] bg-white/[0.02]"
              )}
            >
              <input
                type="radio"
                name="workspaceAiProvider"
                value={key}
                checked={checked}
                disabled={isPending}
                onChange={() => setSelected(key)}
                className="mt-0.5 h-4 w-4 accent-primary-500"
              />
              <span className="min-w-0 flex-1">
                <span className="flex flex-wrap items-center gap-2">
                  <span className="text-sm font-medium text-gray-100">{label(key)}</span>
                  {readiness && readiness !== "unknown" && (
                    <Badge variant={readiness === "ready" ? "success" : "warning"} size="sm" dot>
                      {t(`readiness.${readiness}`)}
                    </Badge>
                  )}
                </span>
                <span className="mt-0.5 block text-xs text-gray-400">{t(`descriptions.${key}`)}</span>
                {readiness && readiness !== "ready" && readiness !== "unknown" && (
                  <span className="mt-1.5 block text-xs leading-snug text-gray-400">
                    {t("fix")}{" "}
                    <code className="break-all rounded bg-black/40 px-1.5 py-0.5 font-mono text-gray-200">
                      {fixCommand(rows, key)}
                    </code>
                  </span>
                )}
              </span>
            </label>
          );
        })}
      </div>

      {rows === null && <p className="mt-2 text-xs text-gray-400">{t("runnerUnknown")}</p>}

      {!dirty && setting.blocker && (
        <p className="mt-3 text-xs leading-snug text-amber-300" data-testid="ai-provider-blocker">
          {t("blocked", {
            provider: label(setting.provider),
            reason: setting.blocker,
            effective: label(setting.effectiveProvider),
          })}
        </p>
      )}
      {!dirty && !setting.blocker && (
        <p className="mt-3 text-xs text-gray-400">
          {t("effective", { provider: label(setting.effectiveProvider) })}
        </p>
      )}
      {choice !== "automatic" && choice !== "claude" && (
        <p className="mt-1 text-xs text-gray-400">{t("qaNote")}</p>
      )}

      <div className="mt-3">
        <Button size="sm" onClick={() => setConfirming(true)} disabled={!dirty || isPending}>
          {t("save")}
        </Button>
      </div>

      <ConfirmDialog
        open={confirming}
        title={t("confirmTitle", { provider: label(choice) })}
        message={
          <>
            <p>{t("confirmMessage", { provider: label(choice) })}</p>
            <label className="mt-3 flex cursor-pointer items-center gap-2.5">
              <input
                type="checkbox"
                checked={applyExisting}
                onChange={(e) => setApplyExisting(e.target.checked)}
                className="h-4 w-4 rounded border-white/20 bg-white/[0.06] accent-primary-500"
              />
              <span className="text-sm text-gray-200">{t("applyExisting")}</span>
            </label>
          </>
        }
        confirmLabel={t("confirm")}
        cancelLabel={tc("cancel")}
        pending={isPending}
        onConfirm={save}
        onCancel={() => setConfirming(false)}
      />
    </GlassCard>
  );
}
