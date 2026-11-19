import { useState } from "react";
import { useTranslations } from "use-intl";
import { Button, GlassCard } from "@/components/ui";
import { canStart, type SetupCheck } from "@/lib/project-setup";
import type { ProjectSetupStep } from "@/lib/types";
import { SetupCheckList } from "./SetupCheckList";

/**
 * Step 5 — everything at once, each item linking back to the step that fixes it, and Start.
 * Start is what lets the runner pull tickets for this project (nothing is pulled before it).
 * While anything is red it is disabled; "Start anyway" lists what is still red or amber and
 * asks once more. A project already started shows its checks again ("Re-run setup checks").
 */
export function ReviewStep({
  checks,
  started,
  starting,
  startError,
  onGoto,
  onStart,
}: {
  checks: SetupCheck[];
  started: boolean;
  starting: boolean;
  startError: string | null;
  onGoto: (step: ProjectSetupStep) => void;
  onStart: (warnings: string[]) => void;
}) {
  const t = useTranslations("dashboard.projectSetup");
  const tr = useTranslations("dashboard.projectSetup.review");
  const [confirmingAnyway, setConfirmingAnyway] = useState(false);

  const blocked = !canStart(checks);
  const problems = checks.filter((c) => c.level !== "ok");
  const describe = (c: SetupCheck) => {
    const params = { ...(c.params ?? {}) };
    if (typeof params.step === "string") params.step = t(`steps.${params.step}.title`);
    return t(`checks.${c.label}`, params);
  };
  const ordered = [
    ...checks.filter((c) => c.level === "error"),
    ...checks.filter((c) => c.level === "warn"),
    ...checks.filter((c) => c.level === "ok"),
  ];

  return (
    <div className="space-y-4" data-testid="setup-review-step">
      <GlassCard padding="lg">
        <h3 className="font-display text-base font-semibold text-white">{tr("title")}</h3>
        <p className="mt-1 text-xs text-gray-400">{started ? tr("introStarted") : tr("intro")}</p>
        <div className="mt-4">
          <SetupCheckList checks={ordered} onGoto={onGoto} testId="setup-review-checks" />
        </div>
      </GlassCard>

      {started ? (
        <p className="text-sm text-emerald-400" data-testid="setup-started">
          {tr("started")}
        </p>
      ) : (
        <GlassCard padding="md">
          <p className="text-sm text-gray-200">{blocked ? tr("blocked") : tr("ready")}</p>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <Button
              onClick={() => onStart([])}
              disabled={blocked || starting}
              data-testid="setup-start"
            >
              {starting ? tr("starting") : tr("start")}
            </Button>
            {problems.length > 0 && !confirmingAnyway && (
              <Button
                variant="ghost"
                onClick={() => setConfirmingAnyway(true)}
                disabled={starting}
                data-testid="setup-start-anyway"
              >
                {tr("startAnyway")}
              </Button>
            )}
          </div>

          {confirmingAnyway && (
            <div
              className="mt-4 space-y-3 rounded-lg border border-amber-500/20 bg-amber-500/[0.06] p-3"
              data-testid="setup-start-anyway-confirm"
            >
              <p className="text-sm text-amber-300">{tr("anywayTitle", { count: problems.length })}</p>
              <ul className="list-inside list-disc space-y-0.5 text-xs text-amber-300">
                {problems.map((c) => (
                  <li key={c.id}>{describe(c)}</li>
                ))}
              </ul>
              <div className="flex flex-wrap gap-2">
                <Button
                  size="sm"
                  variant="danger"
                  disabled={starting}
                  onClick={() => onStart(problems.map(describe))}
                  data-testid="setup-start-anyway-confirm-button"
                >
                  {tr("anywayConfirm")}
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setConfirmingAnyway(false)}>
                  {tr("anywayCancel")}
                </Button>
              </div>
            </div>
          )}
          {startError && <p className="mt-3 text-xs text-red-400">{startError}</p>}
        </GlassCard>
      )}
    </div>
  );
}
