import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { SkillsPage } from "@/components/skills";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";

export default function ProjectSkillsPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.skills"));

  const { data } = useApiQuery(async () => {
    const [skillsResult, agentsResult] = await Promise.allSettled([
      api.skills.list(projectId, ""),
      api.agents.list(projectId, ""),
    ]);
    return {
      skills: skillsResult.status === "fulfilled" ? skillsResult.value : [],
      agents: agentsResult.status === "fulfilled" ? agentsResult.value : [],
    };
  }, [projectId]);

  if (!data) return <DashboardSkeleton />;

  return (
    <div className="px-4 py-6 lg:px-8">
      <div className="mb-6">
        <h2 className="font-display text-lg font-bold text-white">{t("skillsPage.title")}</h2>
        <p className="mt-1 text-sm text-gray-400">{t("skillsPage.subtitle")}</p>
      </div>

      <SkillsPage projectId={projectId} initialSkills={data.skills} agents={data.agents} />
    </div>
  );
}
