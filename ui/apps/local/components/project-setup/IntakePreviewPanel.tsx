import { useState } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, GlassCard } from "@/components/ui";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import type { IntakePreview, IntakePreviewTicket } from "@/lib/types";

type State =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "offline" }
  | { kind: "failed"; error: string | null }
  | { kind: "done"; preview: IntakePreview };

/**
 * "These N tickets would be pulled" — the saved ticket source read by this machine's runner
 * exactly as the next poll would read it, and put through the poll's own intake rules: the
 * tickets that would become tasks, the Stories and their sub-tasks, the tickets already on the
 * board, and what is left out and why. Nothing is created; the tracker is only read.
 */
export function IntakePreviewPanel({
  projectId,
  onPreviewed,
}: {
  projectId: string;
  onPreviewed: () => void;
}) {
  const t = useTranslations("dashboard.projectSetup.preview");
  const withToken = useClientApi();
  const [state, setState] = useState<State>({ kind: "idle" });

  async function run() {
    setState({ kind: "loading" });
    try {
      const response = await withToken((token) => api.trackers.previewIntake(projectId, token));
      if (!response.supported) setState({ kind: "offline" });
      else if (!response.ok || !response.preview) setState({ kind: "failed", error: response.error ?? null });
      else {
        setState({ kind: "done", preview: response.preview });
        onPreviewed();
      }
    } catch (e) {
      setState({ kind: "failed", error: extractErrorMessage(e, "") || null });
    }
  }

  return (
    <GlassCard padding="lg" data-testid="setup-intake-preview">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="font-display text-base font-semibold text-white">{t("title")}</h3>
          <p className="mt-1 max-w-xl text-xs text-gray-400">{t("intro")}</p>
        </div>
        <Button size="sm" onClick={() => void run()} disabled={state.kind === "loading"} data-testid="setup-preview-run">
          {state.kind === "loading" ? t("running") : state.kind === "done" ? t("again") : t("run")}
        </Button>
      </div>

      <div className="mt-4" aria-live="polite">
        {state.kind === "offline" && <p className="text-sm text-amber-300">{t("offline")}</p>}
        {state.kind === "failed" && (
          <p className="text-sm text-red-400" data-testid="setup-preview-error">
            {state.error ? t("failed", { error: state.error }) : t("failedGeneric")}
          </p>
        )}
        {state.kind === "done" && <PreviewResult preview={state.preview} />}
      </div>
    </GlassCard>
  );
}

function PreviewResult({ preview }: { preview: IntakePreview }) {
  const t = useTranslations("dashboard.projectSetup.preview");

  return (
    <div className="space-y-5" data-testid="setup-preview-result">
      <p className="text-sm text-gray-100" data-testid="setup-preview-count">
        {t("count", { count: preview.newTaskCount })}
      </p>

      {preview.tasks.length > 0 && (
        <section>
          <h4 className="mb-2 text-label">{t("tasksTitle", { count: preview.tasks.length })}</h4>
          <TicketTable tickets={preview.tasks} testId="setup-preview-tasks" />
        </section>
      )}

      {preview.stories.map((story) => (
        <section key={story.story.key} data-testid="setup-preview-story">
          <h4 className="mb-1 text-label">
            {t("storyTitle", { key: story.story.key, count: story.members.length })}
          </h4>
          <p className="mb-2 text-xs text-gray-400">
            {story.story.title} · <span className="font-mono">{story.branch}</span>
          </p>
          <TicketTable tickets={story.members} testId="setup-preview-members" />
        </section>
      ))}

      {preview.newTaskCount === 0 && <p className="text-sm text-gray-400">{t("nothing")}</p>}

      {preview.alreadyTracked.length > 0 && (
        <p className="text-xs text-gray-400">{t("tracked", { count: preview.alreadyTracked.length })}</p>
      )}

      {preview.skipped.length > 0 && (
        <details className="text-xs text-gray-400" data-testid="setup-preview-skipped">
          <summary className="cursor-pointer select-none text-gray-300">
            {t("skippedTitle", { count: preview.skipped.length })}
          </summary>
          <ul className="mt-2 space-y-1">
            {preview.skipped.map((s) => (
              <li key={s.key}>
                <span className="font-mono text-cyan-400">{s.key}</span> {s.title}
                {s.status ? ` · ${s.status}` : ""} — {t(`skipReason.${s.reason}`)}
              </li>
            ))}
          </ul>
        </details>
      )}
    </div>
  );
}

function TicketTable({ tickets, testId }: { tickets: IntakePreviewTicket[]; testId: string }) {
  const t = useTranslations("dashboard.projectSetup.preview");

  return (
    <div className="overflow-x-auto rounded-lg border border-white/[0.06]">
      <table className="w-full text-left text-xs" data-testid={testId}>
        <thead className="bg-white/[0.03] text-gray-400">
          <tr>
            <th className="px-3 py-2 font-medium">{t("colKey")}</th>
            <th className="px-3 py-2 font-medium">{t("colTitle")}</th>
            <th className="px-3 py-2 font-medium">{t("colStatus")}</th>
            <th className="px-3 py-2 font-medium">{t("colAssignee")}</th>
            <th className="px-3 py-2 font-medium">{t("colRepo")}</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-white/[0.06]">
          {tickets.map((ticket) => (
            <tr key={ticket.key} data-testid="setup-preview-ticket">
              <td className="px-3 py-2 font-mono">
                {ticket.url ? (
                  <a href={ticket.url} target="_blank" rel="noopener noreferrer" className="text-cyan-400 hover:text-cyan-300">
                    {ticket.key}
                  </a>
                ) : (
                  <span className="text-cyan-400">{ticket.key}</span>
                )}
              </td>
              <td className="max-w-xs truncate px-3 py-2 text-gray-200">
                {ticket.title}
                {ticket.level === "subTask" && (
                  <span className="ml-1.5 text-gray-400" data-testid="setup-preview-subtask-alone">
                    {t("subTaskAlone")}
                  </span>
                )}
              </td>
              <td className="px-3 py-2 text-gray-300">{ticket.status ?? "—"}</td>
              <td className="px-3 py-2 text-gray-300">{ticket.assignee ?? t("you")}</td>
              <td className="px-3 py-2">
                {ticket.spansRepos.length >= 2 ? (
                  <span className="text-gray-200">{ticket.spansRepos.join(" + ")}</span>
                ) : ticket.repoName ? (
                  <span className="text-gray-200">{ticket.repoName}</span>
                ) : (
                  <Badge variant="warning" size="sm" title={ticket.reason ?? undefined}>
                    {ticket.route === "router-at-import" ? t("repoAtImport") : t("repoAsks")}
                  </Badge>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
