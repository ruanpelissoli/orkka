import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { cliForProvider, cliProviderLabel } from "@/lib/model-billing";
import type { ModelBilling, ModelBillingReason } from "@/lib/model-billing";

export interface ModelBillingNoteProps {
  billing: ModelBilling;
  reason: ModelBillingReason;
  /** AiProvider enum name of the model's provider (labelled with the runner's product name). */
  provider: string;
  model: string;
  className?: string;
}

/**
 * One sentence under a model picker saying whether the developer's runner can run a task on
 * this model: green when it can, amber when the task would wait for a sign-in or a model the
 * runner does not have. Every amber case links to the runner settings, because that page is
 * where the fix lives (sign in, connect the machine).
 */
function ModelBillingNote({
  billing,
  reason,
  provider,
  model,
  className,
}: ModelBillingNoteProps) {
  const t = useTranslations("dashboard.modelBilling");
  const cli = cliForProvider(provider);
  const providerName = cliProviderLabel(cli);

  const settingsLink = (
    <Link
      href="/settings/machine"
      className="ml-1 underline decoration-current/40 underline-offset-2 transition-colors hover:text-white"
    >
      {t("settingsLink")}
    </Link>
  );

  if (billing === "runner" && reason === "runnerWaitingSignIn") {
    return (
      <p
        data-billing="runner-waiting"
        className={cn("text-xs leading-snug text-amber-300", className)}
      >
        {t("runnerWaitingSignIn", { provider: providerName })}
        {settingsLink}
      </p>
    );
  }

  if (billing === "runner") {
    return (
      <div data-billing="runner" className={cn("space-y-0.5 text-xs leading-snug", className)}>
        <p className="text-emerald-400">
          {t(
            reason === "runnerReadyStale"
              ? "runnerStale"
              : reason === "runnerOffline"
                ? "runnerUnknown"
                : "runner",
            { provider: providerName }
          )}
        </p>
        {/* Copilot is "free" only from Orkka's side — the run spends the user's premium
            requests, and that is worth one line before they burn a month's quota. */}
        {cli === "copilot" && <p className="text-gray-400">{t("copilotPremiumNote")}</p>}
      </div>
    );
  }

  return (
    <p data-billing="unavailable" className={cn("text-xs leading-snug text-amber-300", className)}>
      {t("unavailableRunnerOnly", { model, provider: providerName })}
      {settingsLink}
    </p>
  );
}

export { ModelBillingNote };
