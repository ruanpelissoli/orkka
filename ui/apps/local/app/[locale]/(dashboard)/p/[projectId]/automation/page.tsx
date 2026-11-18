import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { AutomationPage } from "@/components/automation";
import { DashboardSkeleton, PageError } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";

export default function ProjectAutomationPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.automation"));

  const { data, error, reload } = useApiQuery(async () => {
    const [project, schedules, agents, skills] = await Promise.all([
      api.projects.get(projectId, ""),
      api.schedules.list(projectId, "").catch(() => []),
      api.agents.list(projectId, "").catch(() => []),
      api.skills.list(projectId, "").catch(() => []),
    ]);
    return { project, schedules, agents, skills };
  }, [projectId]);

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <DashboardSkeleton />;

  return (
    <div className="px-4 py-6 lg:px-8">
      <div className="mb-6">
        <h2 className="font-display text-lg font-bold text-white">{t("automationPage.title")}</h2>
        <p className="mt-1 text-sm text-gray-400">{t("automationPage.subtitle")}</p>
      </div>

      <AutomationPage
        project={data.project}
        initialSchedules={data.schedules}
        agents={data.agents}
        skills={data.skills}
      />
    </div>
  );
}
