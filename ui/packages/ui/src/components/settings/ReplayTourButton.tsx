import { useState } from "react";
import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { Button } from "@/components/ui/Button";
import { useProject } from "@/hooks/use-project";
import { useUserPreferencesContext } from "@/components/providers/UserPreferencesProvider";

/**
 * Gives the product tour back to someone who skipped it.
 *
 * There is no separate "start the tour" signal: clearing the member preference is the trigger,
 * and the tour arms itself as soon as a project is on screen — which is why this also
 * navigates. Without a project there is nothing to tour, so the button says so rather than
 * clearing a flag to no effect.
 */
function ReplayTourButton() {
  const t = useTranslations("dashboard");
  const router = useRouter();
  const { projects } = useProject();
  const { setOnboardingTourSeen } = useUserPreferencesContext();
  const [replaying, setReplaying] = useState(false);

  const target = projects[0];

  async function handleReplay() {
    if (!target) return;
    setReplaying(true);
    await setOnboardingTourSeen(false);
    router.push(`/p/${target.slug}`);
  }

  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0">
        <p className="text-sm font-medium text-white">
          {t("preferencesPage.replayTour.label")}
        </p>
        <p className="mt-0.5 text-xs text-gray-400">
          {target
            ? t("preferencesPage.replayTour.description")
            : t("preferencesPage.replayTour.noProject")}
        </p>
      </div>
      <Button
        variant="secondary"
        size="sm"
        disabled={!target}
        loading={replaying}
        onClick={() => void handleReplay()}
      >
        {t("preferencesPage.replayTour.action")}
      </Button>
    </div>
  );
}

export { ReplayTourButton };
