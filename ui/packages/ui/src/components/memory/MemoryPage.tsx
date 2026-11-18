import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, ConfirmDialog, GlassCard, Input, Modal, Select, Textarea, useToast } from "@/components/ui";
import { usePermissions } from "@/hooks/use-permissions";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import { cn, formatRelativeTime } from "@/lib/utils";
import { MEMORY_TYPES, type MemoryType, type ProjectAgent, type ProjectMemory } from "@/lib/types";

interface MemoryPageProps {
  projectId: string;
  agents: ProjectAgent[];
}

type Editor = { mode: "closed" } | { mode: "create" } | { mode: "edit"; memory: ProjectMemory };
type View = "active" | "archived";

const TYPE_VARIANT: Record<MemoryType, "primary" | "info" | "purple" | "success" | "error" | "warning"> = {
  Decision: "primary",
  Convention: "info",
  Preference: "purple",
  Learning: "success",
  Mistake: "error",
  Pattern: "warning",
};

const RULE_TYPES: MemoryType[] = ["Decision", "Convention", "Preference"];

/**
 * The project's memory: what the team (humans + agents) has decided, learned and prefers,
 * retrieved by relevance for every run and chat turn. Humans can read, search, add, edit,
 * pin, archive and delete; the "What would an agent see?" panel previews retrieval for a
 * task so a decision that isn't surfacing can be pinned or reworded.
 */
function MemoryPage({ projectId, agents }: MemoryPageProps) {
  const t = useTranslations("dashboard.memoryPage");
  const withToken = useClientApi();
  const { toast } = useToast();
  const { has } = usePermissions();
  const canEdit = has("AgentsConfigure");

  const [items, setItems] = useState<ProjectMemory[] | null>(null);
  const [total, setTotal] = useState(0);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [q, setQ] = useState("");
  const [debouncedQ, setDebouncedQ] = useState("");
  const [typeFilter, setTypeFilter] = useState<"all" | "rules" | MemoryType>("all");
  const [scope, setScope] = useState<string>("");
  const [view, setView] = useState<View>("active");
  // True from the start: the first page is fetched on mount.
  const [loading, setLoading] = useState(true);

  const [editor, setEditor] = useState<Editor>({ mode: "closed" });
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const [type, setType] = useState<MemoryType>("Learning");
  const [agentId, setAgentId] = useState("");
  const [pinned, setPinned] = useState(false);
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<ProjectMemory | null>(null);

  const [previewOpen, setPreviewOpen] = useState(false);
  const [previewQ, setPreviewQ] = useState("");
  const [previewAgent, setPreviewAgent] = useState("");
  const [preview, setPreview] = useState<ProjectMemory[] | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);

  useEffect(() => {
    const h = setTimeout(() => setDebouncedQ(q.trim()), 300);
    return () => clearTimeout(h);
  }, [q]);

  const query = useMemo(
    () => ({
      q: debouncedQ || undefined,
      type: typeFilter === "all" ? undefined : typeFilter === "rules" ? RULE_TYPES.join(",") : typeFilter,
      scope: scope || undefined,
      archived: view === "archived",
      limit: 50,
    }),
    [debouncedQ, typeFilter, scope, view]
  );

  const loadFailedMessage = t("loadFailed");
  // Fetches a page; the caller marks `loading` first (an event handler, or the render-time reset
  // below for the first page), so the effect that loads a new query sets no state synchronously.
  const fetchPage = useCallback(
    (cursor?: string) =>
      withToken((token) => api.memories.list(projectId, { ...query, cursor }, token))
        .then(
          (page) => {
            setItems((prev) => (cursor && prev ? [...prev, ...page.items] : page.items));
            setNextCursor(page.nextCursor);
            setTotal(page.total);
            setError(null);
          },
          (err: unknown) => {
            setError(extractErrorMessage(err, loadFailedMessage));
          }
        )
        .finally(() => {
          setLoading(false);
        }),
    [withToken, projectId, query, loadFailedMessage]
  );
  const load = useCallback(
    (cursor?: string) => {
      setLoading(true);
      return fetchPage(cursor);
    },
    [fetchPage]
  );

  // A new query (or project) blanks the list during render; the effect then loads its first page.
  const [loadedFor, setLoadedFor] = useState({ fetchPage });
  if (loadedFor.fetchPage !== fetchPage) {
    setLoadedFor({ fetchPage });
    setItems(null);
    setLoading(true);
  }

  useEffect(() => {
    void fetchPage();
  }, [fetchPage]);

  const openCreate = () => {
    setTitle("");
    setContent("");
    setType("Learning");
    setAgentId("");
    setPinned(false);
    setFormError(null);
    setEditor({ mode: "create" });
  };
  const openEdit = (m: ProjectMemory) => {
    setTitle(m.title);
    setContent(m.content);
    setType(m.type);
    setAgentId(m.agentId ?? "");
    setPinned(m.isPinned);
    setFormError(null);
    setEditor({ mode: "edit", memory: m });
  };

  const save = useCallback(async () => {
    if (editor.mode === "closed") return;
    setBusy(true);
    setFormError(null);
    const body = {
      title: title.trim(),
      content: content.trim(),
      type,
      agentId: agentId || null,
      isPinned: pinned,
      isArchived: editor.mode === "edit" ? editor.memory.isArchived : false,
    };
    try {
      if (editor.mode === "create") await withToken((token) => api.memories.create(projectId, body, token));
      else await withToken((token) => api.memories.update(projectId, editor.memory.id, body, token));
      setEditor({ mode: "closed" });
      await load();
    } catch (err) {
      setFormError(extractErrorMessage(err, t("saveFailed")));
    } finally {
      setBusy(false);
    }
  }, [editor, title, content, type, agentId, pinned, withToken, projectId, load, t]);

  const patch = useCallback(
    async (m: ProjectMemory, changes: Partial<{ isPinned: boolean; isArchived: boolean }>) => {
      try {
        await withToken((token) =>
          api.memories.update(
            projectId,
            m.id,
            { title: m.title, content: m.content, type: m.type, agentId: m.agentId, isPinned: m.isPinned, isArchived: m.isArchived, ...changes },
            token
          )
        );
        await load();
      } catch (err) {
        toast(extractErrorMessage(err, t("saveFailed")), "error");
      }
    },
    [withToken, projectId, load, toast, t]
  );

  const remove = useCallback(async () => {
    if (!pendingDelete) return;
    setBusy(true);
    try {
      await withToken((token) => api.memories.delete(projectId, pendingDelete.id, token));
      setPendingDelete(null);
      await load();
    } catch (err) {
      toast(extractErrorMessage(err, t("deleteFailed")), "error");
    } finally {
      setBusy(false);
    }
  }, [pendingDelete, withToken, projectId, load, toast, t]);

  const runPreview = useCallback(async () => {
    setPreviewBusy(true);
    try {
      setPreview(await withToken((token) => api.memories.preview(projectId, previewQ, previewAgent || null, token)));
    } catch (err) {
      toast(extractErrorMessage(err, t("loadFailed")), "error");
    } finally {
      setPreviewBusy(false);
    }
  }, [withToken, projectId, previewQ, previewAgent, toast, t]);

  return (
    <div className="space-y-4" data-testid="memory-page">
      <GlassCard padding="md">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <p className="max-w-2xl text-xs text-gray-400">{t("intro")}</p>
          <div className="flex items-center gap-2">
            <Button size="sm" variant="secondary" onClick={() => setPreviewOpen(true)} data-testid="memory-preview-open">
              {t("previewButton")}
            </Button>
            {canEdit && (
              <Button size="sm" onClick={openCreate} data-testid="memory-add">
                {t("add")}
              </Button>
            )}
          </div>
        </div>
        <div className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Input label={t("search")} value={q} onChange={(e) => setQ(e.target.value)} placeholder={t("searchPlaceholder")} />
          <Select label={t("filterType")} value={typeFilter} onChange={(e) => setTypeFilter(e.target.value as typeof typeFilter)}>
            <option value="all">{t("typeAll")}</option>
            <option value="rules">{t("typeRules")}</option>
            {MEMORY_TYPES.map((k) => (
              <option key={k} value={k}>
                {t(`types.${k}`)}
              </option>
            ))}
          </Select>
          <Select label={t("filterScope")} value={scope} onChange={(e) => setScope(e.target.value)}>
            <option value="">{t("scopeAll")}</option>
            <option value="team">{t("scopeTeam")}</option>
            {agents.map((a) => (
              <option key={a.id} value={a.id}>
                {t("scopeAgent", { name: a.name })}
              </option>
            ))}
          </Select>
          <Select label={t("filterView")} value={view} onChange={(e) => setView(e.target.value as View)}>
            <option value="active">{t("viewActive")}</option>
            <option value="archived">{t("viewArchived")}</option>
          </Select>
        </div>
      </GlassCard>

      {error && <p className="text-sm text-red-400">{error}</p>}

      <GlassCard padding="none">
        {items === null ? (
          <div className="space-y-2 p-4">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="h-12 animate-pulse rounded bg-white/[0.04]" />
            ))}
          </div>
        ) : items.length === 0 ? (
          <p className="p-6 text-center text-sm text-gray-400">{debouncedQ ? t("emptySearch") : t("empty")}</p>
        ) : (
          <ul className="divide-y divide-white/[0.04]">
            {items.map((m) => (
              <li key={m.id} className={cn("px-4 py-3", m.isPinned && "bg-primary-500/[0.04]")} data-testid="memory-row">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge size="sm" variant={TYPE_VARIANT[m.type] ?? "default"}>
                        {t(`types.${m.type}`)}
                      </Badge>
                      {m.isPinned && (
                        <Badge size="sm" variant="primary">
                          {t("pinned")}
                        </Badge>
                      )}
                      <span className="text-sm font-medium text-gray-100">{m.title}</span>
                    </div>
                    <p className="mt-1 whitespace-pre-wrap text-sm text-gray-300">{m.content}</p>
                    <p className="mt-1 text-xs text-gray-400">
                      {m.agentId ? t("privateTo", { name: m.agentName ?? "?" }) : t("sharedTeam")}
                      {" · "}
                      {t(`sources.${m.source}`)}
                      {" · "}
                      {formatRelativeTime(m.createdAt)}
                      {" · "}
                      {t("usedCount", { count: m.useCount })}
                    </p>
                  </div>
                  {canEdit && (
                    <div className="flex shrink-0 flex-wrap items-center gap-1">
                      <Button size="sm" variant="ghost" onClick={() => void patch(m, { isPinned: !m.isPinned })} data-testid="memory-pin">
                        {m.isPinned ? t("unpin") : t("pin")}
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => openEdit(m)}>
                        {t("edit")}
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => void patch(m, { isArchived: !m.isArchived })}>
                        {m.isArchived ? t("restore") : t("archive")}
                      </Button>
                      <Button size="sm" variant="ghost" className="text-red-400" onClick={() => setPendingDelete(m)}>
                        {t("delete")}
                      </Button>
                    </div>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
        {nextCursor && (
          <div className="border-t border-white/[0.04] p-3 text-center">
            <Button size="sm" variant="ghost" onClick={() => void load(nextCursor)} loading={loading}>
              {t("loadMore", { shown: items?.length ?? 0, total })}
            </Button>
          </div>
        )}
      </GlassCard>

      {/* Editor */}
      <Modal open={editor.mode !== "closed"} onClose={() => !busy && setEditor({ mode: "closed" })} title={editor.mode === "edit" ? t("editTitle") : t("createTitle")}>
        <div className="space-y-4">
          <Input label={t("fieldTitle")} value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} placeholder={t("titlePlaceholder")} autoFocus />
          <Textarea label={t("fieldContent")} value={content} onChange={(e) => setContent(e.target.value)} rows={5} maxLength={4000} placeholder={t("contentPlaceholder")} />
          <div className="grid gap-3 sm:grid-cols-2">
            <Select label={t("fieldType")} value={type} onChange={(e) => setType(e.target.value as MemoryType)}>
              {MEMORY_TYPES.map((k) => (
                <option key={k} value={k}>
                  {t(`types.${k}`)}
                </option>
              ))}
            </Select>
            <Select label={t("fieldScope")} value={agentId} onChange={(e) => setAgentId(e.target.value)}>
              <option value="">{t("scopeTeam")}</option>
              {agents.map((a) => (
                <option key={a.id} value={a.id}>
                  {t("scopeAgent", { name: a.name })}
                </option>
              ))}
            </Select>
          </div>
          <p className="text-xs text-gray-400">{t("typeHint")}</p>
          <label className="flex cursor-pointer items-center gap-2 text-xs text-gray-200">
            <input type="checkbox" checked={pinned} onChange={(e) => setPinned(e.target.checked)} />
            {t("pinLabel")}
          </label>
          {formError && <p className="text-sm text-red-400">{formError}</p>}
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="ghost" onClick={() => setEditor({ mode: "closed" })} disabled={busy}>
              {t("cancel")}
            </Button>
            <Button onClick={() => void save()} disabled={busy || title.trim().length === 0 || content.trim().length === 0} loading={busy} data-testid="memory-save">
              {editor.mode === "edit" ? t("save") : t("create")}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Retrieval preview */}
      <Modal open={previewOpen} onClose={() => setPreviewOpen(false)} title={t("previewTitle")}>
        <div className="space-y-3">
          <p className="text-xs text-gray-400">{t("previewIntro")}</p>
          <Textarea label={t("previewQuery")} value={previewQ} onChange={(e) => setPreviewQ(e.target.value)} rows={3} placeholder={t("previewPlaceholder")} />
          <div className="grid gap-3 sm:grid-cols-[1fr_auto] sm:items-end">
            <Select label={t("previewAgent")} value={previewAgent} onChange={(e) => setPreviewAgent(e.target.value)}>
              <option value="">{t("previewAnyAgent")}</option>
              {agents.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </Select>
            <Button size="sm" onClick={() => void runPreview()} loading={previewBusy} data-testid="memory-preview-run">
              {t("previewRun")}
            </Button>
          </div>
          {preview !== null && (
            <div className="max-h-[50vh] space-y-2 overflow-y-auto" data-testid="memory-preview-results">
              {preview.length === 0 ? (
                <p className="text-sm text-gray-400">{t("previewEmpty")}</p>
              ) : (
                preview.map((m, i) => (
                  <div key={m.id} className="rounded-lg border border-white/[0.06] p-2.5 text-xs">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-mono text-gray-400">{i + 1}.</span>
                      <Badge size="sm" variant={TYPE_VARIANT[m.type] ?? "default"}>
                        {t(`types.${m.type}`)}
                      </Badge>
                      <span className="text-gray-100">{m.title}</span>
                    </div>
                    <p className="mt-1 text-gray-300">{m.content}</p>
                  </div>
                ))
              )}
            </div>
          )}
        </div>
      </Modal>

      <ConfirmDialog
        open={pendingDelete !== null}
        title={t("deleteTitle")}
        message={t("deleteMessage", { title: pendingDelete?.title ?? "" })}
        confirmLabel={t("delete")}
        cancelLabel={t("cancel")}
        danger
        pending={busy}
        onConfirm={() => void remove()}
        onCancel={() => setPendingDelete(null)}
      />
    </div>
  );
}

export { MemoryPage };
