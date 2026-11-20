import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { Button, Modal, Select } from "@/components/ui";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { cn } from "@/lib/utils";
import type { TrackerImportPreview, TrackerImportStatus } from "@/lib/types";

interface ImportTicketButtonProps {
  /** The projects a ticket can be imported into — one on a project board; the workspace's on
   * the workspace board (the dialog then asks which). */
  projects: { slug: string; name: string }[];
  /** Opens the imported task (or the one already on the board). */
  onImported?: (taskId: string, projectSlug: string) => void;
}

type Phase =
  | { kind: "idle" }
  | { kind: "reading" }
  | { kind: "preview"; preview: TrackerImportPreview }
  | { kind: "queued"; status: TrackerImportStatus }
  | { kind: "done"; status: TrackerImportStatus }
  | { kind: "error"; message: string; preview?: TrackerImportPreview };

/** How often the dialog asks whether the poll has made the import yet, and for how long. */
const POLL_MS = 3000;
const GIVE_UP_MS = 5 * 60_000;

/**
 * "Import ticket": start work from a ticket's key (or link) in the project's tracker — Jira, Azure
 * DevOps or GitHub Projects, whichever the project's ticket source uses. The ticket is read on
 * the developer's own runner and previewed with its sub-tasks (open ones ticked); importing
 * queues it for the next poll, which brings in its description, attachments and comments and
 * makes a task — or, with sub-tasks picked, a Story on one branch — whoever it is assigned to.
 */
function ImportTicketButton({ projects, onImported }: ImportTicketButtonProps) {
  const t = useTranslations("dashboard.importTicket");
  const withToken = useClientApi();
  const [open, setOpen] = useState(false);
  const [project, setProject] = useState(projects.length === 1 ? projects[0].slug : "");
  const [key, setKey] = useState("");
  const [phase, setPhase] = useState<Phase>({ kind: "idle" });
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const startedAtRef = useRef(0);

  const close = useCallback(() => {
    setOpen(false);
    setKey("");
    setPhase({ kind: "idle" });
  }, []);

  const read = useCallback(async () => {
    if (!project || !key.trim()) return;
    setPhase({ kind: "reading" });
    try {
      const res = await withToken((token) => api.trackers.previewImport(project, key.trim(), token));
      if (!res.supported) setPhase({ kind: "error", message: t("runnerOffline") });
      else if (!res.ok || !res.preview) setPhase({ kind: "error", message: res.error ?? t("readFailed") });
      else {
        setPicked(new Set(res.preview.subTasks.filter((s) => s.selected).map((s) => s.key)));
        setPhase({ kind: "preview", preview: res.preview });
      }
    } catch (e) {
      setPhase({ kind: "error", message: e instanceof Error ? e.message : t("readFailed") });
    }
  }, [project, key, withToken, t]);

  const settle = useCallback(
    (status: TrackerImportStatus) => {
      if (status.state === "done") setPhase({ kind: "done", status });
      else if (status.state === "failed") setPhase({ kind: "error", message: status.error ?? t("importFailed") });
      // A fresh object every time, so the effect below asks again.
      else setPhase({ kind: "queued", status: { ...status } });
    },
    [t]
  );

  // While queued, ask every few seconds whether the poll has made it yet (closing the dialog,
  // or any other phase, stops asking).
  useEffect(() => {
    if (phase.kind !== "queued" || Date.now() - startedAtRef.current > GIVE_UP_MS) return;
    const status = phase.status;
    const timer = setTimeout(async () => {
      try {
        settle(await withToken((token) => api.trackers.getImport(project, status.id, token)));
      } catch {
        settle(status);
      }
    }, POLL_MS);
    return () => clearTimeout(timer);
  }, [phase, project, withToken, settle]);

  const importNow = useCallback(async () => {
    if (phase.kind !== "preview") return;
    const preview = phase.preview;
    try {
      const status = await withToken((token) =>
        api.trackers.requestImport(project, preview.key, [...picked], token)
      );
      startedAtRef.current = Date.now();
      settle(status);
    } catch (e) {
      setPhase({ kind: "error", message: e instanceof Error ? e.message : t("importFailed"), preview });
    }
  }, [phase, project, picked, withToken, settle, t]);

  const togglePick = (subKey: string) =>
    setPicked((prev) => {
      const next = new Set(prev);
      if (!next.delete(subKey)) next.add(subKey);
      return next;
    });

  const openTask = (taskId: string) => {
    onImported?.(taskId, project);
    close();
  };

  return (
    <>
      <Button size="sm" variant="secondary" onClick={() => setOpen(true)} data-testid="import-ticket-button">
        {t("button")}
      </Button>
      <Modal open={open} onClose={close} title={t("title")} className="max-w-2xl">
        <div className="space-y-4" data-testid="import-ticket-dialog">
          <p className="text-xs text-gray-400">{t("intro")}</p>
          <form
            className="flex flex-wrap items-center gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              void read();
            }}
          >
            {projects.length > 1 && (
              <Select
                size="sm"
                value={project}
                onChange={(e) => {
                  setProject(e.target.value);
                  setPhase({ kind: "idle" });
                }}
                aria-label={t("project")}
                data-testid="import-ticket-project"
              >
                <option value="">{t("pickProject")}</option>
                {projects.map((p) => (
                  <option key={p.slug} value={p.slug}>
                    {p.name}
                  </option>
                ))}
              </Select>
            )}
            <input
              value={key}
              onChange={(e) => setKey(e.target.value)}
              placeholder={t("keyPlaceholder")}
              aria-label={t("key")}
              autoFocus
              className={cn(
                "min-w-[220px] flex-1 rounded-lg px-3 py-1.5 text-sm text-gray-200",
                "bg-white/[0.04] border border-white/[0.06] placeholder:text-gray-500",
                "focus:border-primary-500/50 focus:outline-none"
              )}
              data-testid="import-ticket-key"
            />
            <Button
              size="sm"
              type="submit"
              disabled={!project || !key.trim() || phase.kind === "reading" || phase.kind === "queued"}
              data-testid="import-ticket-read"
            >
              {phase.kind === "reading" ? t("reading") : t("read")}
            </Button>
          </form>

          {phase.kind === "error" && (
            <p className="rounded-lg border border-red-500/20 bg-red-500/5 px-3 py-2 text-xs text-red-300" role="alert" data-testid="import-ticket-error">
              {phase.message}
            </p>
          )}

          {phase.kind === "preview" && (
            <PreviewBody preview={phase.preview} picked={picked} onToggle={togglePick} onOpenExisting={openTask} />
          )}

          {phase.kind === "preview" && !phase.preview.existingTaskId && (
            <div className="flex items-center justify-end gap-2">
              <span className="text-xs text-gray-400" data-testid="import-ticket-will-be">
                {picked.size > 0 ? t("willBeStory", { count: picked.size }) : t("willBeTask")}
              </span>
              <Button size="sm" onClick={() => void importNow()} data-testid="import-ticket-import">
                {t("import")}
              </Button>
            </div>
          )}

          {phase.kind === "queued" && (
            <p className="text-sm text-gray-300" role="status" data-testid="import-ticket-queued">
              {t("queued", { key: phase.status.ticketKey })}
            </p>
          )}

          {phase.kind === "done" && (
            <div className="flex items-center justify-between gap-2" role="status" data-testid="import-ticket-done">
              <p className="text-sm text-gray-200">{t("done", { key: phase.status.ticketKey })}</p>
              {phase.status.taskId && (
                <Button size="sm" onClick={() => openTask(phase.status.taskId!)}>
                  {t("openTask")}
                </Button>
              )}
            </div>
          )}
        </div>
      </Modal>
    </>
  );
}

function PreviewBody({
  preview,
  picked,
  onToggle,
  onOpenExisting,
}: {
  preview: TrackerImportPreview;
  picked: ReadonlySet<string>;
  onToggle: (key: string) => void;
  onOpenExisting: (taskId: string) => void;
}) {
  const t = useTranslations("dashboard.importTicket");
  return (
    <div className="space-y-3" data-testid="import-ticket-preview">
      <div className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-3">
        <div className="flex flex-wrap items-center gap-2">
          {preview.url ? (
            <a href={preview.url} target="_blank" rel="noreferrer" className="font-mono text-xs text-cyan-400 hover:text-cyan-300">
              {preview.key}
            </a>
          ) : (
            <span className="font-mono text-xs text-cyan-400">{preview.key}</span>
          )}
          <span className="text-sm font-medium text-gray-100">{preview.title}</span>
        </div>
        <p className="mt-1 text-xs text-gray-400">
          {[preview.type, preview.status, preview.assignedToMe ? t("assignedToYou") : t("notAssignedToYou"),
            preview.repoName ? t("repo", { name: preview.repoName }) : null,
            t("attachments", { count: preview.attachments.length })]
            .filter(Boolean)
            .join(" · ")}
        </p>
        {preview.description && (
          <p className="mt-2 line-clamp-4 whitespace-pre-line text-xs text-gray-300">{preview.description}</p>
        )}
        {preview.existingTaskId && (
          <div className="mt-2 flex items-center justify-between gap-2 text-xs text-amber-300" data-testid="import-ticket-existing">
            <span>{t("alreadyOnBoard")}</span>
            <Button size="sm" variant="secondary" onClick={() => onOpenExisting(preview.existingTaskId!)}>
              {t("openTask")}
            </Button>
          </div>
        )}
      </div>

      {preview.subTasks.length > 0 && (
        <div>
          <p className="mb-1 text-label">{t("subTasks", { count: preview.subTasks.length })}</p>
          <ul className="divide-y divide-white/[0.06] rounded-xl border border-white/[0.06]">
            {preview.subTasks.map((s) => {
              const disabled = !s.found || s.existingTaskId !== null;
              return (
                <li key={s.key} className="flex items-center gap-2 px-3 py-2 text-xs" data-testid="import-ticket-subtask">
                  <input
                    type="checkbox"
                    checked={picked.has(s.key)}
                    disabled={disabled}
                    onChange={() => onToggle(s.key)}
                    aria-label={s.key}
                    className="h-3.5 w-3.5 rounded border-white/[0.1] bg-white/[0.04] accent-primary-500"
                  />
                  <span className="font-mono text-cyan-400">{s.key}</span>
                  <span className="min-w-0 flex-1 truncate text-gray-200">{s.title}</span>
                  <span className="shrink-0 text-gray-400">
                    {!s.found
                      ? t("notFound")
                      : s.existingTaskId
                        ? t("onBoard")
                        : [s.status, s.assignedToMe ? t("yours") : t("notYours")].filter(Boolean).join(" · ")}
                  </span>
                </li>
              );
            })}
          </ul>
          <p className="mt-1 text-xs text-gray-400">{t("subTasksHint")}</p>
        </div>
      )}
    </div>
  );
}

export { ImportTicketButton };
