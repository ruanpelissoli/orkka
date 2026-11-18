import { useParams, useSearchParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import type { ProjectDocument } from "@/lib/types";
import { DocumentsBrowser } from "./DocumentsBrowser";
import { AgentChatDrawer } from "../team/AgentChatDrawer";

export default function DocumentsPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const d = useSearchParams().get("d");
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.documents"));

  const { data: documents } = useApiQuery(
    () => api.documents.list(projectId, "").catch(() => [] as ProjectDocument[]),
    [projectId]
  );

  if (!documents) return <DashboardSkeleton />;

  return (
    <div className="flex flex-col h-[calc(100vh-4rem)] p-4 lg:py-6 lg:pl-4 lg:pr-6">
      <DocumentsBrowser
        projectId={projectId}
        initialDocuments={documents}
        initialDocumentId={d ?? null}
      />
      {/* Side chat with the PM — ask for documents or tasks without leaving the page */}
      <AgentChatDrawer projectId={projectId} role="PM" />
    </div>
  );
}
