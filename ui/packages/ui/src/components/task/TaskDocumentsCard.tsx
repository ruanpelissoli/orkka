import { useEffect, useMemo, useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { cn, latestDocumentVersions } from "@/lib/utils";
import { GlassCard } from "@/components/ui";
import { usePermissions } from "@/hooks/use-permissions";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { updateTask } from "@/lib/actions";
import type { ProjectDocument, Task } from "@/lib/types";

interface TaskDocumentsCardProps {
  task: Task;
  projectId: string;
}

/**
 * Sidebar card for the documents attached to a task's context. Attached documents are
 * injected into agent prompts when the task runs. The picker expands inline (no floating
 * dropdown — the sidebar stack made those clip against the cards below).
 */
function TaskDocumentsCard({ task, projectId }: TaskDocumentsCardProps) {
  const t = useTranslations("dashboard.taskDocuments");
  const { has } = usePermissions();
  const canEdit = has("TasksEdit");
  const withToken = useClientApi();
  const [isPending, startTransition] = useTransition();

  const [selectedIds, setSelectedIds] = useState<string[]>(task.contextDocumentIds ?? []);
  const [documents, setDocuments] = useState<ProjectDocument[] | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [filter, setFilter] = useState("");

  // Sync when the parent re-fetches the task
  const [prevContextDocumentIds, setPrevContextDocumentIds] = useState(task.contextDocumentIds);
  if (task.contextDocumentIds !== prevContextDocumentIds) {
    setPrevContextDocumentIds(task.contextDocumentIds);
    setSelectedIds(task.contextDocumentIds ?? []);
  }

  // The attached-list needs titles even before the picker is opened
  useEffect(() => {
    if (documents !== null) return;
    if (selectedIds.length === 0 && !pickerOpen) return;
    let cancelled = false;
    withToken((token) => api.documents.list(projectId, token))
      .then((docs) => {
        if (!cancelled) setDocuments(docs);
      })
      .catch(() => {
        if (!cancelled) setDocuments([]);
      });
    return () => {
      cancelled = true;
    };
  }, [documents, selectedIds.length, pickerOpen, projectId, withToken]);

  const byId = useMemo(
    () => new Map((documents ?? []).map((d) => [d.id, d])),
    [documents]
  );

  const grouped = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    // Offer only the newest copy of each folder+title; an attached older copy is still
    // removable from the attached list above, which reads the full document set.
    const visible = latestDocumentVersions(documents ?? []).filter(
      (d) => !needle || d.title.toLowerCase().includes(needle) || d.path.toLowerCase().includes(needle)
    );
    const groups = new Map<string, ProjectDocument[]>();
    for (const doc of visible) {
      const key = doc.path || "/";
      const list = groups.get(key) ?? [];
      list.push(doc);
      groups.set(key, list);
    }
    return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [documents, filter]);

  const persist = (ids: string[]) => {
    const previous = selectedIds;
    setSelectedIds(ids);
    startTransition(async () => {
      try {
        await updateTask(task.id, { contextDocumentIds: ids });
      } catch {
        setSelectedIds(previous);
      }
    });
  };

  const toggle = (id: string) => {
    persist(
      selectedIds.includes(id)
        ? selectedIds.filter((s) => s !== id)
        : [...selectedIds, id]
    );
  };

  return (
    <GlassCard padding="md">
      <div className="mb-3 flex items-center justify-between">
        <h4 className="text-label">
          {t("title")}
        </h4>
        {canEdit && (
          <button
            onClick={() => setPickerOpen((v) => !v)}
            disabled={isPending}
            className={cn(
              "flex items-center gap-1 rounded-lg px-2 py-1 text-xs transition-colors",
              pickerOpen
                ? "bg-white/[0.08] text-white"
                : "text-gray-400 hover:bg-white/[0.04] hover:text-gray-200"
            )}
          >
            {pickerOpen ? (
              t("done")
            ) : (
              <>
                <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
                </svg>
                {t("attachDocuments")}
              </>
            )}
          </button>
        )}
      </div>

      {/* Attached documents */}
      {selectedIds.length === 0 && !pickerOpen ? (
        <p className="text-xs text-gray-400">{t("empty")}</p>
      ) : (
        selectedIds.length > 0 && (
          <div className="space-y-1">
            {selectedIds.map((id) => {
              const doc = byId.get(id);
              return (
                <div
                  key={id}
                  className="group flex items-center gap-2 rounded-lg px-2 py-1.5 transition-colors hover:bg-white/[0.03]"
                >
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" className="shrink-0 text-amber-400/80">
                    <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
                    <path d="M14 2v6h6M16 13H8M16 17H8" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                  <Link
                    href={`/p/${projectId}/documents?d=${id}`}
                    className="min-w-0 flex-1"
                    title={doc ? (doc.path ? `${doc.path}/${doc.title}` : doc.title) : undefined}
                  >
                    <span className="block truncate text-xs text-gray-300 transition-colors group-hover:text-white">
                      {doc?.title ?? t("unknownDocument")}
                    </span>
                    {doc?.path && (
                      <span className="block truncate font-mono text-xs text-gray-400">
                        {doc.path}
                      </span>
                    )}
                  </Link>
                  {canEdit && (
                    <button
                      onClick={() => toggle(id)}
                      disabled={isPending}
                      aria-label={t("remove")}
                      className="shrink-0 rounded p-1 text-gray-400 opacity-0 transition-all hover:bg-red-500/10 hover:text-red-400 group-hover:opacity-100"
                    >
                      <svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
                        <path d="M12 4L4 12M4 4l8 8" strokeLinecap="round" />
                      </svg>
                    </button>
                  )}
                </div>
              );
            })}
          </div>
        )
      )}

      {/* Inline picker */}
      {pickerOpen && (
        <div className="mt-3 border-t border-white/[0.06] pt-3">
          <input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder={t("filterPlaceholder")}
            className={cn(
              "mb-2 w-full rounded-lg border border-white/[0.08] bg-white/[0.03] px-2.5 py-1.5",
              "text-xs text-gray-200 placeholder:text-gray-500",
              "focus:border-primary-500/50 focus:outline-none"
            )}
          />
          <div className="max-h-56 space-y-1.5 overflow-y-auto">
            {documents === null ? (
              <p className="px-2 py-3 text-center text-xs text-gray-400">{t("loading")}</p>
            ) : documents.length === 0 ? (
              <p className="px-2 py-3 text-center text-xs text-gray-400">{t("noDocuments")}</p>
            ) : grouped.length === 0 ? (
              <p className="px-2 py-3 text-center text-xs text-gray-400">{t("noMatches")}</p>
            ) : (
              grouped.map(([folder, docs]) => (
                <div key={folder}>
                  <p className="px-1 py-0.5 text-label">
                    {folder}
                  </p>
                  {docs.map((doc) => {
                    const checked = selectedIds.includes(doc.id);
                    return (
                      <button
                        key={doc.id}
                        onClick={() => toggle(doc.id)}
                        disabled={isPending}
                        className="flex w-full items-center gap-2 rounded-lg px-1.5 py-1.5 text-left transition-colors hover:bg-white/[0.04]"
                      >
                        <span
                          className={cn(
                            "flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded border",
                            checked
                              ? "border-amber-500/60 bg-amber-500/20 text-amber-400"
                              : "border-white/[0.12]"
                          )}
                        >
                          {checked && (
                            <svg width="9" height="9" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3">
                              <path d="M5 13l4 4L19 7" strokeLinecap="round" strokeLinejoin="round" />
                            </svg>
                          )}
                        </span>
                        <span className="truncate text-xs text-gray-300">{doc.title}</span>
                      </button>
                    );
                  })}
                </div>
              ))
            )}
          </div>
        </div>
      )}

      <p className="mt-3 text-xs leading-relaxed text-gray-400">{t("hint")}</p>
    </GlassCard>
  );
}

export { TaskDocumentsCard };
