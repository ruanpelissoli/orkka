import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { BrandKitPanel } from "@/components/design";
import { DashboardSkeleton, PageError } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import { DesignGallery } from "./DesignGallery";

export default function DesignPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.design"));

  const { data: assets, error, reload } = useApiQuery(
    () => api.designAssets.list(projectId, ""),
    [projectId]
  );

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!assets) return <DashboardSkeleton />;

  return (
    <div className="flex flex-col h-[calc(100vh-4rem)] p-4 lg:py-6 lg:pl-4 lg:pr-6">
      {/* Above the gallery: the palette every asset below was drawn from. Renders nothing
          for a project that has no kit. */}
      <div className="mb-4 shrink-0">
        <BrandKitPanel projectId={projectId} />
      </div>
      <DesignGallery assets={assets} projectId={projectId} />
    </div>
  );
}
