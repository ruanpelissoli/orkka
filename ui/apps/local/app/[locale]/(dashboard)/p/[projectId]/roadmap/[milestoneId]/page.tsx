import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { MilestoneDetailClient } from "@/components/roadmap";
import { DashboardSkeleton, NotFoundView } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";

export default function MilestonePage() {
  const { projectId, milestoneId } = useParams<{ projectId: string; milestoneId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.milestone"));

  const { data: detail, error } = useApiQuery(
    () => api.milestones.get(projectId, milestoneId, ""),
    [projectId, milestoneId]
  );

  // Unlike the roadmap list, there is no useful degraded render here — without the milestone
  // there is nothing to show and nothing to plan into.
  if (error) return <NotFoundView />;
  if (!detail) return <DashboardSkeleton />;

  return (
    <div className="px-4 py-6 lg:px-8">
      <MilestoneDetailClient projectId={projectId} initial={detail} />
    </div>
  );
}
