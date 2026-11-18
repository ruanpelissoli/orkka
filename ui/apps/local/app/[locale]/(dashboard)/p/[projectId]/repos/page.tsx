import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { DashboardSkeleton, PageError } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import type { Project } from "@/lib/types";
import { RepoFileBrowser } from "./RepoFileBrowser";
import { LocalFolderConnect } from "./LocalFolderConnect";
import { BaseFolderPanel } from "./BaseFolderPanel";

export default function ReposPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.repos"));

  const { data, error, reload } = useApiQuery(async () => {
    const [repos, project] = await Promise.all([
      api.repos.list(projectId, ""),
      // Only for the base folder; the page still works without it.
      api.projects.get(projectId, "").catch((): Project | null => null),
    ]);
    return { repos, project };
  }, [projectId]);

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <DashboardSkeleton />;

  const { repos, project } = data;

  return (
    <div className="flex flex-col h-[calc(100vh-4rem)] p-4 lg:py-6 lg:pl-4 lg:pr-6">
      <div className="mb-6 space-y-4">
        <BaseFolderPanel
          projectId={projectId}
          project={
            project
              ? { baseFolder: project.baseFolder ?? null, baseFolderShape: project.baseFolderShape ?? null }
              : null
          }
          repos={repos}
        />
        <LocalFolderConnect projectId={projectId} repos={repos} />
      </div>

      <RepoFileBrowser projectId={projectId} repos={repos} />
    </div>
  );
}
