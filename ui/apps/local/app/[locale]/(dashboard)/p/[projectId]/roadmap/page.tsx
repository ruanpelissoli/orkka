import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { RoadmapClient } from "@/components/roadmap";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import type { MilestoneListResponse } from "@/lib/types";

export default function RoadmapPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.roadmap"));

  const { data } = useApiQuery(async () => {
    // Both take the raw route param, so neither depends on the other.
    const [projectResult, milestonesResult] = await Promise.allSettled([
      api.projects.get(projectId, ""),
      api.milestones.list(projectId, ""),
    ]);
    const milestones: MilestoneListResponse =
      milestonesResult.status === "fulfilled"
        ? milestonesResult.value
        : { milestones: [], backlog: { total: 0, done: 0, inFlight: 0, needsAttention: 0, notStarted: 0 } };
    return {
      // SignalR board groups are keyed by the project ULID, so resolve the project even
      // when the route param is a slug; fall back to the raw param (legacy ULID URLs).
      projectUlid: projectResult.status === "fulfilled" ? projectResult.value.id : projectId,
      milestones,
    };
  }, [projectId]);

  if (!data) return <DashboardSkeleton />;

  return (
    <div className="px-4 py-6 lg:px-8">
      <div className="mb-6">
        <h2 className="font-display text-lg font-bold text-white">{t("roadmapPage.title")}</h2>
        <p className="mt-1 text-sm text-gray-400">{t("roadmapPage.subtitle")}</p>
      </div>

      <RoadmapClient
        projectId={projectId}
        projectUlid={data.projectUlid}
        initialMilestones={data.milestones.milestones}
        initialBacklog={data.milestones.backlog}
      />
    </div>
  );
}
