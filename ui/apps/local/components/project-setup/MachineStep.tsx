import { useTranslations } from "use-intl";
import { Button, GlassCard } from "@/components/ui";
import { Link } from "@/i18n/navigation";
import { ProviderSignInList } from "@/components/runner/ProviderSignInList";
import { machineChecks } from "@/lib/project-setup";
import type { MyRunnerStatus, ProjectRepo, RepoAccessCheck } from "@/lib/types";
import { SetupCheckList } from "./SetupCheckList";

/**
 * Step 2 — is this machine ready to work the project? The runner online, an AI CLI signed in
 * (with the exact sign-in command for each one that is not), who commits are authored as, every
 * repository reachable with the developer's own git credentials, and the forge CLI that opens
 * pull requests signed in for the repositories that need one. "Re-check" asks the runner again.
 */
export function MachineStep({
  runner,
  access,
  repos,
  rechecking,
  onRecheck,
}: {
  runner: MyRunnerStatus | null;
  access: RepoAccessCheck | null;
  repos: ProjectRepo[];
  rechecking: boolean;
  onRecheck: () => void;
}) {
  const t = useTranslations("dashboard.projectSetup.machine");
  const checks = machineChecks({ runner, access, repos });

  return (
    <div className="space-y-4" data-testid="setup-machine-step">
      <GlassCard padding="md">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <h3 className="text-sm font-medium text-gray-100">{t("title")}</h3>
            <p className="mt-1 text-xs text-gray-400">{t("intro")}</p>
          </div>
          <Button size="sm" variant="secondary" onClick={onRecheck} disabled={rechecking} data-testid="setup-recheck">
            {rechecking ? t("rechecking") : t("recheck")}
          </Button>
        </div>
        <div className="mt-4">
          {runner === null ? (
            <p className="text-sm text-gray-400">{t("loading")}</p>
          ) : (
            <SetupCheckList checks={checks} testId="setup-machine-checks" />
          )}
        </div>
        {access && !access.supported && runner?.online && (
          <p className="mt-3 text-xs text-gray-400">{t("accessUnsupported")}</p>
        )}
      </GlassCard>

      {runner?.online && (runner.providers ?? []).length > 0 && (
        <GlassCard padding="md">
          <h3 className="text-sm font-medium text-gray-100">{t("aiTitle")}</h3>
          <p className="mt-1 text-xs text-gray-400">{t("aiIntro")}</p>
          <ProviderSignInList providers={runner.providers ?? []} />
        </GlassCard>
      )}

      <p className="text-xs text-gray-400">
        {t("doctorHint")}{" "}
        <code className="rounded bg-black/40 px-1.5 py-0.5 font-mono text-gray-200">./orkka doctor</code>{" "}
        <Link href="/settings/machine" className="text-cyan-400 hover:text-cyan-300">
          {t("machinePage")}
        </Link>
      </p>
    </div>
  );
}
