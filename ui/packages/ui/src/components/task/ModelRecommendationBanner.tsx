import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button, GlassCard } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { resolveModelRecommendation } from "@/lib/actions";
import type { Task } from "@/lib/types";

/**
 * The PM review's advisory cheaper-model suggestion. Apply sets the task's own model
 * override (the same field the create form offers); Dismiss clears the suggestion. The
 * agent's configured model is never touched — that boundary is the whole feature.
 */
// The backend only ever recommends catalog ids; the two it can emit today are mapped here,
// anything else falls back to the raw id rather than hiding the banner.
const MODEL_NAMES: Record<string, string> = {
  "claude-haiku-4-5": "Claude Haiku 4.5",
  "claude-sonnet-4-6": "Claude Sonnet 4.6",
};

interface ModelRecommendationBannerProps {
  task: Task;
  /** Called with the updated task after apply/dismiss so the parent refreshes its copy. */
  onResolved?: (task: Task) => void;
}

function ModelRecommendationBanner({ task, onResolved }: ModelRecommendationBannerProps) {
  const t = useTranslations("dashboard.modelRecommendation");
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  if (!task.recommendedModel) return null;
  const modelName = MODEL_NAMES[task.recommendedModel] ?? task.recommendedModel;

  function resolve(action: "apply" | "dismiss") {
    startTransition(async () => {
      const res = await resolveModelRecommendation(task.id, action);
      if (res.error) {
        setError(res.error);
        return;
      }
      setError(null);
      if (res.task) onResolved?.(res.task);
    });
  }

  return (
    <GlassCard padding="md" accentColor="#34d399">
      <div className="flex items-center gap-3 flex-wrap">
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#34d399" strokeWidth="2" className="shrink-0">
          <path d="M13 2L3 14h7l-1 8 10-12h-7l1-8z" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <div className="min-w-0 flex-1">
          <h3 className="font-display text-sm font-semibold text-white">
            {t("title", { model: modelName })}
          </h3>
          <p className="mt-0.5 text-xs text-gray-400">{t("hint")}</p>
          {error && <p className="mt-1 text-xs text-red-300">{error}</p>}
        </div>
        <RequirePermission permission="TasksEdit">
          <div className="flex items-center gap-2 shrink-0">
            <Button
              variant="success"
              size="sm"
              loading={isPending}
              disabled={isPending}
              onClick={() => resolve("apply")}
            >
              {t("apply", { model: modelName })}
            </Button>
            <Button variant="ghost" size="sm" disabled={isPending} onClick={() => resolve("dismiss")}>
              {t("dismiss")}
            </Button>
          </div>
        </RequirePermission>
      </div>
    </GlassCard>
  );
}

export { ModelRecommendationBanner };
