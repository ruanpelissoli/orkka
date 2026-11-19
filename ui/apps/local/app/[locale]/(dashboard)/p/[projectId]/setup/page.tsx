import { useTranslations } from "use-intl";
import { ProjectSetup } from "@/components/project-setup/ProjectSetup";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { useParams } from "@/lib/navigation";

/** `/p/{slug}/setup?step=…` — the project's guided setup; nothing is pulled before Start. */
export default function ProjectSetupPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.projectSetup"));
  return <ProjectSetup projectId={projectId} />;
}
