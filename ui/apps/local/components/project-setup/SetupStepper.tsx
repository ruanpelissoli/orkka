import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { SETUP_STEPS, stepLevel, type SetupCheck } from "@/lib/project-setup";
import type { ProjectSetupStep } from "@/lib/types";

/**
 * The setup's five steps across the top: where you are, what you confirmed, and the worst
 * check of each step (green / amber / red). Every step can be visited at any time — the order
 * is a suggestion, not a lock.
 */
export function SetupStepper({
  current,
  confirmed,
  checks,
  onSelect,
}: {
  current: ProjectSetupStep;
  confirmed: readonly string[];
  checks: readonly SetupCheck[];
  onSelect: (step: ProjectSetupStep) => void;
}) {
  const t = useTranslations("dashboard.projectSetup");

  return (
    <nav aria-label={t("stepsLabel")} data-testid="setup-stepper">
      <ol className="grid grid-cols-2 gap-2 sm:grid-cols-5">
        {SETUP_STEPS.map((step, index) => {
          const active = step === current;
          const done = confirmed.includes(step);
          const level = stepLevel(checks, step);
          return (
            <li key={step}>
              <button
                type="button"
                onClick={() => onSelect(step)}
                aria-current={active ? "step" : undefined}
                data-testid={`setup-step-${step}`}
                data-done={done ? "true" : "false"}
                className={cn(
                  "flex w-full items-center gap-2 rounded-lg border px-3 py-2 text-left transition-colors",
                  active
                    ? "border-primary-500/50 bg-primary-500/10"
                    : "border-white/[0.06] bg-white/[0.02] hover:border-white/[0.12]"
                )}
              >
                <span
                  className={cn(
                    "flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-xs font-medium",
                    done ? "bg-emerald-500/20 text-emerald-300" : "bg-white/[0.06] text-gray-300"
                  )}
                >
                  {done ? "✓" : index + 1}
                </span>
                <span className="min-w-0 flex-1 truncate text-sm text-gray-200">{t(`steps.${step}.title`)}</span>
                {level && level !== "ok" && (
                  <span
                    className={cn(
                      "h-2 w-2 shrink-0 rounded-full",
                      level === "error" ? "bg-red-400" : "bg-amber-400"
                    )}
                    title={t(`level.${level}`)}
                  />
                )}
              </button>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
