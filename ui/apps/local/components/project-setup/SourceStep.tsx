import { useState } from "react";
import { useTranslations } from "use-intl";
import { Button, GlassCard, SegmentedControl } from "@/components/ui";
import { TicketSourceCard } from "@/components/project/TicketSourceCard";
import { AddTrackerConnectionForm, TrackerAddCommands } from "@/components/settings/TrackerConnections";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import { LOCAL_USER_ID } from "@/lib/local-auth-client";
import { sourceChecks, sourceChoiceOf, type SourceChoice } from "@/lib/project-setup";
import type { Project, ProjectRepo, ProjectTrackerConfig, TrackerConnection } from "@/lib/types";
import { IntakePreviewPanel } from "./IntakePreviewPanel";
import { SetupCheckList } from "./SetupCheckList";

type Choice = Exclude<SourceChoice, null>;

function kindOf(connection: TrackerConnection): Choice {
  return connection.kind === "ado" ? "ado" : connection.kind === "github" ? "github" : "jira";
}

/**
 * Step 3 — where the tickets come from: Jira, Azure DevOps, GitHub Projects, or none (tasks
 * written by hand only). A tracker with no connection on this machine is connected right here
 * (the settings page's write-only form: the token goes to this machine's secret store; GitHub
 * can use the gh login). Then the project's ticket source — the settings card itself — and the
 * dry run of what would be pulled. Nothing is pulled before Start, whatever is saved here.
 */
export function SourceStep({
  project,
  repos,
  connections,
  config,
  previewed,
  onConfigChanged,
  onConnectionAdded,
  onPreviewed,
}: {
  project: Project;
  repos: ProjectRepo[];
  connections: TrackerConnection[];
  config: ProjectTrackerConfig | null;
  previewed: boolean;
  onConfigChanged: (config: ProjectTrackerConfig | null) => void;
  onConnectionAdded: (connection: TrackerConnection) => void;
  onPreviewed: () => void;
}) {
  const t = useTranslations("dashboard.projectSetup.source");
  const withToken = useClientApi();
  const [choice, setChoice] = useState<Choice>(
    () => sourceChoiceOf(config, project.setupSteps) ?? (connections[0] ? kindOf(connections[0]) : "jira")
  );
  const [removing, setRemoving] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);

  const kindConnections = choice === "none" ? [] : connections.filter((c) => kindOf(c) === choice);
  const configForKind = config && config.connection && kindOf(config.connection) === choice ? config : null;
  const checks = sourceChecks({ project, trackerConfig: config, previewed });

  async function removeSource() {
    setRemoving(true);
    setRemoveError(null);
    try {
      await withToken((token) => api.trackers.deleteProjectConfig(project.id, token));
      onConfigChanged(null);
    } catch (e) {
      setRemoveError(extractErrorMessage(e, t("removeFailed")));
    } finally {
      setRemoving(false);
    }
  }

  return (
    <div className="space-y-4" data-testid="setup-source-step">
      <GlassCard padding="md">
        <h3 className="text-sm font-medium text-gray-100">{t("title")}</h3>
        <p className="mt-1 text-xs text-gray-400">{t("intro")}</p>
        <div className="mt-3">
          <SegmentedControl<Choice>
            aria-label={t("title")}
            value={choice}
            onChange={setChoice}
            options={[
              { value: "jira", label: t("kind.jira"), testId: "setup-source-jira" },
              { value: "ado", label: t("kind.ado"), testId: "setup-source-ado" },
              { value: "github", label: t("kind.github"), testId: "setup-source-github" },
              { value: "none", label: t("kind.none"), testId: "setup-source-none" },
            ]}
          />
        </div>
        <p className="mt-3 text-xs text-gray-400">{t("gateNote")}</p>
      </GlassCard>

      {choice === "none" && (
        <GlassCard padding="md" data-testid="setup-source-none-panel">
          <p className="text-sm text-gray-200">{t("noneBody")}</p>
          {config && (
            <div className="mt-3 space-y-2">
              <p className="text-xs text-amber-300">{t("noneHasSource")}</p>
              <Button size="sm" variant="danger" onClick={() => void removeSource()} disabled={removing}>
                {removing ? t("removing") : t("removeSource")}
              </Button>
              {removeError && <p className="text-xs text-red-400">{removeError}</p>}
            </div>
          )}
        </GlassCard>
      )}

      {choice !== "none" && kindConnections.length === 0 && (
        <GlassCard padding="md" data-testid="setup-source-connect">
          <h3 className="text-sm font-medium text-gray-100">{t("connectTitle", { tracker: t(`kind.${choice}`) })}</h3>
          <p className="mt-1 text-xs text-gray-400">{t("connectIntro")}</p>
          <div className="mt-4">
            <AddTrackerConnectionForm key={choice} kind={choice} onAdded={onConnectionAdded} />
          </div>
          <details className="mt-3 text-xs text-gray-400">
            <summary className="cursor-pointer select-none text-gray-300">{t("connectWithCli")}</summary>
            <div className="mt-3">
              <TrackerAddCommands />
            </div>
          </details>
        </GlassCard>
      )}

      {choice !== "none" && kindConnections.length > 0 && (
        <>
          <TicketSourceCard
            key={`${choice}:${configForKind?.id ?? "new"}`}
            projectId={project.id}
            initialConfig={configForKind}
            connections={kindConnections}
            repos={repos}
            viewerUserId={LOCAL_USER_ID}
            ownerName={null}
            onSaved={onConfigChanged}
          />
          {configForKind ? (
            <IntakePreviewPanel projectId={project.id} onPreviewed={onPreviewed} />
          ) : (
            <p className="text-xs text-gray-400">{t("saveToPreview")}</p>
          )}
        </>
      )}

      <SetupCheckList checks={checks.filter((c) => c.level !== "ok")} testId="setup-source-checks" />
    </div>
  );
}
