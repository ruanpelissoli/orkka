import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { ActivityPage as ActivityPageView } from "@/components/activity";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";

export default function ActivityPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.activity"));

  const { data } = useApiQuery(async () => {
    // All three take the raw route param, so none depends on the other.
    const [projectResult, executionsResult, agentsResult] = await Promise.allSettled([
      api.projects.get(projectId, ""),
      api.executions.listForProject(projectId, { limit: 500 }, ""),
      api.agents.list(projectId, ""),
    ]);
    return {
      // SignalR board groups are keyed by the project ULID, so resolve the project even
      // when the route param is a slug; fall back to the raw param (legacy ULID URLs).
      projectUlid: projectResult.status === "fulfilled" ? projectResult.value.id : projectId,
      initial: executionsResult.status === "fulfilled" ? executionsResult.value : null,
      // Only populates the agent filter — losing it costs one control, not the page.
      agents: agentsResult.status === "fulfilled" ? agentsResult.value : [],
    };
  }, [projectId]);

  if (!data) return <DashboardSkeleton />;

  return (
    <div className="px-4 py-6 lg:px-8">
      <div className="mb-6">
        <h2 className="font-display text-lg font-bold text-white">{t("activityPage.title")}</h2>
        <p className="mt-1 text-sm text-gray-400">{t("activityPage.subtitle")}</p>
      </div>

      <ActivityPageView
        projectId={projectId}
        projectUlid={data.projectUlid}
        initial={data.initial}
        agents={data.agents}
      />
    </div>
  );
}
