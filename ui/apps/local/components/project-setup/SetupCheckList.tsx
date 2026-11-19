import { useTranslations } from "use-intl";
import { CopyableCommand } from "@/components/ui";
import { cn } from "@/lib/utils";
import type { CheckLevel, SetupCheck } from "@/lib/project-setup";
import type { ProjectSetupStep } from "@/lib/types";

const DOT: Record<CheckLevel, string> = {
  ok: "bg-emerald-400",
  warn: "bg-amber-400",
  error: "bg-red-400",
};

const TEXT: Record<CheckLevel, string> = {
  ok: "text-gray-200",
  warn: "text-amber-300",
  error: "text-red-300",
};

/**
 * A list of setup checks: green / amber / red, each with what the machine said and the exact
 * command that fixes it. With `onGoto`, every item links back to the step where it is fixed
 * (the Review checklist).
 */
export function SetupCheckList({
  checks,
  onGoto,
  testId,
}: {
  checks: SetupCheck[];
  onGoto?: (step: ProjectSetupStep) => void;
  testId?: string;
}) {
  const t = useTranslations("dashboard.projectSetup");

  if (checks.length === 0) return null;

  return (
    <ul className="space-y-2" data-testid={testId ?? "setup-checks"}>
      {checks.map((check) => {
        const params = { ...(check.params ?? {}) };
        if (typeof params.step === "string") params.step = t(`steps.${params.step}.title`);
        return (
          <li
            key={check.id}
            data-testid="setup-check"
            data-check={check.id}
            data-level={check.level}
            className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-3 py-2.5"
          >
            <div className="flex items-start gap-2.5">
              <span
                aria-hidden
                className={cn("mt-1.5 h-2 w-2 shrink-0 rounded-full", DOT[check.level])}
              />
              <div className="min-w-0 flex-1 space-y-1.5">
                <p className={cn("text-sm", TEXT[check.level])}>
                  <span className="sr-only">{t(`level.${check.level}`)}: </span>
                  {t(`checks.${check.label}`, params)}
                </p>
                {check.detail && <p className="break-words text-xs text-gray-400">{check.detail}</p>}
                {check.fix && <CopyableCommand command={check.fix} />}
              </div>
              {onGoto && check.level !== "ok" && (
                <button
                  type="button"
                  onClick={() => onGoto(check.step)}
                  className="shrink-0 text-xs text-primary-400 transition-colors hover:text-primary-300"
                >
                  {t("goToStep", { step: t(`steps.${check.step}.title`) })}
                </button>
              )}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
