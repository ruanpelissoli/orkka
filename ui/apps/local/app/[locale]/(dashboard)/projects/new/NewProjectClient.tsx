import { useRouter } from "@/i18n/navigation";
import { invalidateApiCache } from "@/lib/api";
import { useProject } from "@/hooks/use-project";
import { CreateProjectFromFolder } from "@/components/projects/CreateProjectFromFolder";
import { refreshData } from "@/lib/data-refresh";

/** Creates the project from a folder on this machine, then opens its board. */
export function NewProjectClient() {
  const router = useRouter();
  const { refreshProjects } = useProject();
  return (
    <CreateProjectFromFolder
      onCreated={(project) => {
        invalidateApiCache();
        // The sidebar and project switcher read this list; the new project belongs in it.
        void refreshProjects();
        router.push(`/p/${project.slug}/board`);
        refreshData();
      }}
    />
  );
}
