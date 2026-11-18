import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui/GlassCard";
import type { Project } from "@/lib/types";
import { PipelineAutomationSettings } from "./PipelineAutomationSettings";

interface ProjectAutomationCardProps {
  project: Project;
}

/** The settings card around `PipelineAutomationSettings`. */
function ProjectAutomationCard({ project }: ProjectAutomationCardProps) {
  const t = useTranslations("dashboard");

  return (
    <GlassCard padding="lg">
      <h2 className="font-display text-lg font-semibold text-white mb-4">
        {t("settingsPage.pipelineAutomation")}
      </h2>
      <PipelineAutomationSettings project={project} />
    </GlassCard>
  );
}

export { ProjectAutomationCard };
