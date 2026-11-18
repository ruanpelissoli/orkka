import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { MemoryPage } from "@/components/memory";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";

export default function ProjectMemoryPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.memory"));

  const { data: agents } = useApiQuery(
    () => api.agents.list(projectId, "").catch(() => []),
    [projectId]
  );

  if (!agents) return <DashboardSkeleton />;

  return (
    <div className="px-4 py-6 lg:px-8">
      <div className="mb-6">
        <h2 className="font-display text-lg font-bold text-white">{t("memoryPage.title")}</h2>
        <p className="mt-1 text-sm text-gray-400">{t("memoryPage.subtitle")}</p>
      </div>

      <MemoryPage projectId={projectId} agents={agents} />
    </div>
  );
}
