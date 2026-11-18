import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { DashboardSkeleton, PageError } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import { DiagramGallery } from "./DiagramGallery";
import { AgentChatDrawer } from "../team/AgentChatDrawer";

export default function ArchitectPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.architect"));

  const { data: diagrams, error, reload } = useApiQuery(
    () => api.diagrams.list(projectId, ""),
    [projectId]
  );

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!diagrams) return <DashboardSkeleton />;

  return (
    <>
      {/* Gallery applies its own page container; the editor takes the full area */}
      <DiagramGallery diagrams={diagrams} projectId={projectId} />
      {/* Side chat with the Solution Architect — request diagrams without leaving the board */}
      <AgentChatDrawer projectId={projectId} role="SolutionArchitect" />
    </>
  );
}
