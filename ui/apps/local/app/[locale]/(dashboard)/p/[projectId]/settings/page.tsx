import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { ProjectAutomationCard } from "@/components/project/ProjectAutomationCard";
import { ProjectLanguageSetting } from "@/components/project/ProjectLanguageSetting";
import { TicketSourceCard } from "@/components/project/TicketSourceCard";
import { DashboardSkeleton, PageError } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { LOCAL_USER_ID } from "@/lib/local-auth-client";
import { api } from "@/lib/api";
import { ProjectSettingsActions } from "./ProjectSettingsActions";

export default function ProjectSettingsPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.projectSettings"));

  const { data, error, reload } = useApiQuery(async () => {
    const project = await api.projects.get(projectId, "");
    // The ticket source is optional and its reads must never take the settings page down with
    // them — an empty card is the right answer to any of these failing.
    const [trackerConfig, trackerConnections, repos] = await Promise.all([
      api.trackers.getProjectConfig(project.id, "").catch(() => null),
      api.trackers.connections("").catch(() => []),
      api.repos.list(project.id, "").catch(() => []),
    ]);
    return { project, trackerConfig, trackerConnections, repos };
  }, [projectId]);

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <DashboardSkeleton />;

  const { project, trackerConfig, trackerConnections, repos } = data;

  return (
    <div className="mx-auto max-w-3xl p-4 lg:p-8 space-y-8">
      {/* General */}
      <GlassCard padding="lg">
        <h2 className="font-display text-lg font-semibold text-white mb-4">
          {t("settingsPage.general")}
        </h2>
        <ProjectSettingsActions project={project} />
        <div className="mt-6 border-t border-white/10 pt-5">
          <ProjectLanguageSetting project={project} />
        </div>
      </GlassCard>

      {/* Pipeline automation */}
      <ProjectAutomationCard project={project} />

      {/* Ticket source — Jira / Azure DevOps epics feeding this project */}
      <TicketSourceCard
        projectId={project.id}
        initialConfig={trackerConfig}
        connections={trackerConnections}
        repos={repos}
        viewerUserId={LOCAL_USER_ID}
        ownerName={null}
      />

      {/* Danger Zone */}
      <ProjectSettingsActions project={project} dangerZone />
    </div>
  );
}
