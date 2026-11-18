import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { GlassCard, Button, Badge, Markdown, PdfViewer, EmptyState, Dropdown, ConfirmDialog, Select, useToast } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { FileTree } from "@/components/repos/FileTree";
import { api, ApiError } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { createDocument, deleteDocument, renameDocumentFolder, restoreDocumentRevision } from "@/lib/actions";
import {
  DOCUMENT_UPLOAD_ACCEPT,
  buildFileTree,
  cn,
  documentUploadTitle,
  formatRelativeTime,
  isFileDocument,
  isPdfUpload,
  looksBinary,
} from "@/lib/utils";
import type {
  DocumentRevisionDetail,
  DocumentRevisionMeta,
  ProjectDocument,
  ProjectDocumentDetail,
} from "@/lib/types";
import { DocumentEditor } from "./DocumentEditor";
import { DocumentDiffView } from "./DocumentDiffView";

interface DocumentsBrowserProps {
  projectId: string;
  initialDocuments: ProjectDocument[];
  initialDocumentId: string | null;
}

type ViewMode = "view" | "edit" | "new";

function documentTreePath(doc: ProjectDocument): string {
  // File documents keep their real extension in the tree so a PDF reads as one at a glance.
  const extension = isFileDocument(doc) ? ".pdf" : ".md";
  return (doc.path ? doc.path + "/" : "") + doc.title + extension;
}

// Text uploads become markdown documents in the database; the server caps content at 200k
// characters. PDFs skip all of this — they are stored as the file they are.
const MAX_CONTENT_CHARS = 200_000;

// Mirrors DocumentRules.MaxFileSizeBytes — catching it here saves a 20 MB round trip.
const MAX_FILE_BYTES = 20 * 1024 * 1024;

function DocumentsBrowser({
  projectId,
  initialDocuments,
  initialDocumentId,
}: DocumentsBrowserProps) {
  const t = useTranslations("dashboard.documents");
  const tFolders = useTranslations("dashboard.folders");
  const withToken = useClientApi();
  const { toast } = useToast();

  const [documents, setDocuments] = useState<ProjectDocument[]>(initialDocuments);
  const [selectedId, setSelectedId] = useState<string | null>(initialDocumentId);
  const [detail, setDetail] = useState<ProjectDocumentDetail | null>(null);
  // A preselected document starts loading on mount.
  const [isLoadingDetail, setIsLoadingDetail] = useState(initialDocumentId !== null);
  const [mode, setMode] = useState<ViewMode>("view");
  const [showHistory, setShowHistory] = useState(false);
  const [revisions, setRevisions] = useState<DocumentRevisionMeta[]>([]);
  const [viewingRevision, setViewingRevision] = useState<DocumentRevisionDetail | null>(null);
  const [showDiff, setShowDiff] = useState(false);
  const [diffBase, setDiffBase] = useState<DocumentRevisionDetail | null>(null);
  const [isRestoring, setIsRestoring] = useState(false);
  const [isDownloading, setIsDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Rename-folder dialog: folders are virtual (derived from document paths).
  const [renameFolderOpen, setRenameFolderOpen] = useState(false);
  const [renameFrom, setRenameFrom] = useState("");
  const [renameTo, setRenameTo] = useState("");
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [isUploading, setIsUploading] = useState(false);
  const uploadInputRef = useRef<HTMLInputElement>(null);

  const folderPaths = useMemo(
    () => [...new Set(documents.map((d) => d.path).filter(Boolean))].sort(),
    [documents]
  );

  const tree = useMemo(
    () => buildFileTree(documents.map(documentTreePath)),
    [documents]
  );

  const docByTreePath = useMemo(() => {
    const map = new Map<string, ProjectDocument>();
    for (const doc of documents) map.set(documentTreePath(doc), doc);
    return map;
  }, [documents]);

  const selectedTreePath = useMemo(() => {
    const doc = documents.find((d) => d.id === selectedId);
    return doc ? documentTreePath(doc) : null;
  }, [documents, selectedId]);

  const refreshList = useCallback(async () => {
    const docs = await withToken((token) => api.documents.list(projectId, token))
      .catch(() => null);
    if (docs) setDocuments(docs);
  }, [projectId, withToken]);

  // A chat-drawer turn that creates or edits documents triggers refreshData() — the
  // page query refetches a fresh list; sync it into state (during render) so it appears
  // immediately.
  const [prevInitialDocuments, setPrevInitialDocuments] = useState(initialDocuments);
  if (initialDocuments !== prevInitialDocuments) {
    setPrevInitialDocuments(initialDocuments);
    setDocuments(initialDocuments);
  }

  // If the refreshed list shows the open document changed (agent edit from the side
  // chat), reload it — but never while the user is editing or viewing an old revision.
  const selectedListVersion = documents.find((d) => d.id === selectedId)?.currentVersion;
  useEffect(() => {
    if (mode !== "view" || viewingRevision || !detail || detail.id !== selectedId) return;
    if (selectedListVersion === undefined || selectedListVersion === detail.currentVersion) return;
    let cancelled = false;
    withToken((token) => api.documents.get(projectId, detail.id, token))
      .then((doc) => {
        if (!cancelled) setDetail(doc);
      })
      .catch(() => {
        /* keep the current view */
      });
    return () => {
      cancelled = true;
    };
  }, [selectedListVersion, mode, viewingRevision, detail, selectedId, projectId, withToken]);

  // Reset the detail pane when the selection changes (during render, before the load
  // below starts): clear it when nothing is selected, else show the loading state.
  const selectionKey = `${projectId}:${selectedId ?? ""}`;
  const [prevSelectionKey, setPrevSelectionKey] = useState(selectionKey);
  if (selectionKey !== prevSelectionKey) {
    setPrevSelectionKey(selectionKey);
    if (!selectedId) {
      setDetail(null);
    } else {
      setIsLoadingDetail(true);
      setError(null);
      setShowHistory(false);
      setViewingRevision(null);
    }
  }

  // Load detail when selection changes
  useEffect(() => {
    if (!selectedId) return;
    let cancelled = false;
    withToken((token) => api.documents.get(projectId, selectedId, token))
      .then((doc) => {
        if (!cancelled) setDetail(doc);
      })
      .catch(() => {
        if (!cancelled) setError(t("loadFailed"));
      })
      .finally(() => {
        if (!cancelled) setIsLoadingDetail(false);
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, selectedId, withToken, t]);

  // Load revision list when the history panel opens
  useEffect(() => {
    if (!showHistory || !selectedId) return;
    let cancelled = false;
    withToken((token) => api.documents.listRevisions(projectId, selectedId, token))
      .then((revs) => {
        if (!cancelled) setRevisions(revs);
      })
      .catch(() => {
        if (!cancelled) setRevisions([]);
      });
    return () => {
      cancelled = true;
    };
  }, [showHistory, selectedId, projectId, withToken, detail?.currentVersion]);

  const handleSelectTreePath = useCallback(
    (path: string) => {
      const doc = docByTreePath.get(path);
      if (doc) {
        setSelectedId(doc.id);
        setMode("view");
      }
    },
    [docByTreePath]
  );

  const handleSaved = useCallback(
    async (saved: ProjectDocumentDetail) => {
      setMode("view");
      setDetail(saved);
      setSelectedId(saved.id);
      await refreshList();
    },
    [refreshList]
  );

  // Uploaded files become regular documents (root folder, title = file name) — they get the
  // same revisions and tree placement as any other document. Text files are decoded into a
  // markdown body; a PDF is sent to the server as-is and rendered by the viewer instead.
  const handleUploadFiles = useCallback(
    async (files: FileList | null) => {
      if (!files || files.length === 0) return;
      setIsUploading(true);
      let lastCreated: ProjectDocumentDetail | null = null;
      for (const file of Array.from(files)) {
        if (isPdfUpload(file.name)) {
          if (file.size > MAX_FILE_BYTES) {
            toast(t("uploadFileTooLarge", { name: file.name }), "error");
            continue;
          }
          try {
            lastCreated = await withToken((token) =>
              api.documents.uploadFile(projectId, file, "", token)
            );
          } catch (err) {
            const conflict = err instanceof ApiError && err.status === 409;
            toast(
              conflict
                ? t("uploadConflict", { name: file.name })
                : t("uploadFailed", { name: file.name }),
              "error"
            );
          }
          continue;
        }

        // `accept` on the input is only a dialog hint — a binary picked through "All files"
        // would otherwise be decoded as UTF-8 and saved as garbage.
        const title = documentUploadTitle(file.name);
        if (!title) {
          toast(t("uploadUnsupported", { name: file.name }), "error");
          continue;
        }
        let content: string;
        try {
          content = await file.text();
        } catch {
          toast(t("uploadFailed", { name: file.name }), "error");
          continue;
        }
        if (!content.trim()) {
          toast(t("uploadFailed", { name: file.name }), "error");
          continue;
        }
        if (looksBinary(content)) {
          toast(t("uploadUnsupported", { name: file.name }), "error");
          continue;
        }
        if (content.length > MAX_CONTENT_CHARS) {
          toast(t("uploadTooLarge", { name: file.name }), "error");
          continue;
        }
        try {
          const doc = await createDocument(projectId, { title, path: "", content });
          if (!doc) {
            toast(t("uploadConflict", { name: file.name }), "error");
            continue;
          }
          lastCreated = doc;
        } catch {
          toast(t("uploadFailed", { name: file.name }), "error");
        }
      }
      setIsUploading(false);
      if (lastCreated) {
        setDetail(lastCreated);
        setSelectedId(lastCreated.id);
        setMode("view");
      }
      await refreshList();
    },
    [projectId, refreshList, t, toast, withToken]
  );

  const handleRenameFolder = useCallback(async () => {
    const from = renameFrom;
    const to = renameTo.trim();
    setRenameFolderOpen(false);
    setRenameTo("");
    if (!from || !to || to === from) return;
    try {
      await renameDocumentFolder(projectId, from, to);
      await refreshList();
    } catch {
      toast(tFolders("renameFailed"), "error");
    }
  }, [renameFrom, renameTo, projectId, refreshList, tFolders, toast]);

  const handleDelete = useCallback(async () => {
    if (!detail) return;
    setConfirmingDelete(false);
    try {
      await deleteDocument(projectId, detail.id);
      setSelectedId(null);
      setDetail(null);
      await refreshList();
    } catch {
      setError(t("deleteFailed"));
    }
  }, [detail, projectId, refreshList, t]);

  // A file document is already a file — "export" is just handing back what was uploaded.
  const handleDownloadFile = useCallback(async () => {
    if (!detail?.fileName) return;
    setIsDownloading(true);
    setError(null);
    try {
      const blob = await withToken((token) =>
        api.documents.getFile(projectId, detail.id, token)
      );
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = detail.fileName;
      anchor.click();
      URL.revokeObjectURL(url);
    } catch {
      setError(t("downloadFailed"));
    } finally {
      setIsDownloading(false);
    }
  }, [detail, projectId, withToken, t]);

  const handleExport = useCallback(async (format: string) => {
    if (!detail) return;
    setIsDownloading(true);
    setError(null);
    try {
      // Markdown is the stored source of truth — exported straight from memory.
      // PDF and Word are rendered server-side from the same content.
      const blob =
        format === "md"
          ? new Blob([detail.content], { type: "text/markdown;charset=utf-8" })
          : await withToken((token) =>
              format === "docx"
                ? api.documents.downloadDocx(projectId, detail.id, token)
                : api.documents.downloadPdf(projectId, detail.id, token)
            );
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = `${detail.title}.${format}`;
      anchor.click();
      URL.revokeObjectURL(url);
    } catch {
      setError(t("downloadFailed"));
    } finally {
      setIsDownloading(false);
    }
  }, [detail, projectId, withToken, t]);

  const handleViewRevision = useCallback(
    async (version: number) => {
      if (!detail) return;
      try {
        const revision = await withToken((token) =>
          api.documents.getRevision(projectId, detail.id, version, token)
        );
        setViewingRevision(revision);
        setShowDiff(false);
        setDiffBase(null);
      } catch {
        setError(t("loadFailed"));
      }
    },
    [detail, projectId, withToken, t]
  );

  // Diff compares the viewed revision against its immediate predecessor.
  const handleToggleDiff = useCallback(async () => {
    if (!detail || !viewingRevision) return;
    if (showDiff) {
      setShowDiff(false);
      return;
    }
    if (diffBase?.version === viewingRevision.version - 1) {
      setShowDiff(true);
      return;
    }
    try {
      const base = await withToken((token) =>
        api.documents.getRevision(projectId, detail.id, viewingRevision.version - 1, token)
      );
      setDiffBase(base);
      setShowDiff(true);
    } catch {
      setError(t("loadFailed"));
    }
  }, [detail, viewingRevision, showDiff, diffBase, projectId, withToken, t]);

  const handleRestoreRevision = useCallback(
    async (version: number) => {
      if (!detail || isRestoring) return;
      setIsRestoring(true);
      setError(null);
      try {
        const restored = await restoreDocumentRevision(projectId, detail.id, version);
        setDetail(restored);
        setViewingRevision(null);
        await refreshList();
      } catch {
        setError(t("restoreFailed"));
      } finally {
        setIsRestoring(false);
      }
    },
    [detail, projectId, isRestoring, refreshList, t]
  );

  const isFileDetail = detail !== null && isFileDocument(detail);

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div className="flex items-center justify-between">
        <h1 className="font-display text-xl font-semibold text-white">{t("title")}</h1>
        <div className="flex items-center gap-1.5">
          {folderPaths.length > 0 && (
            <RequirePermission permission="DocumentsManage">
              <Button
                size="sm"
                variant="ghost"
                onClick={() => {
                  setRenameFrom(folderPaths[0] ?? "");
                  setRenameTo("");
                  setRenameFolderOpen(true);
                }}
              >
                {tFolders("renameFolder")}
              </Button>
            </RequirePermission>
          )}
          <RequirePermission permission="DocumentsManage">
            <input
              ref={uploadInputRef}
              type="file"
              accept={DOCUMENT_UPLOAD_ACCEPT}
              multiple
              className="hidden"
              onChange={(e) => {
                handleUploadFiles(e.target.files);
                e.target.value = "";
              }}
            />
            <Button
              size="sm"
              variant="ghost"
              onClick={() => uploadInputRef.current?.click()}
              disabled={isUploading}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M12 16V4m0 0L7 9m5-5l5 5M4 20h16" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              {isUploading ? t("uploading") : t("upload")}
            </Button>
          </RequirePermission>
          <RequirePermission permission="DocumentsManage">
            <Button
              size="sm"
              onClick={() => {
                setMode("new");
                setViewingRevision(null);
                setShowHistory(false);
              }}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
              </svg>
              {t("newDocument")}
            </Button>
          </RequirePermission>
        </div>
      </div>

      {error && (
        <div className="rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-400">
          {error}
        </div>
      )}

      <div className="flex min-h-0 flex-1 flex-col gap-4 lg:flex-row">
        {/* Tree pane — compact scrollable strip above the document on mobile */}
        <GlassCard className="max-h-44 w-full shrink-0 overflow-y-auto !p-2 lg:max-h-none lg:w-64">
          {documents.length === 0 ? (
            <div className="px-3 py-6 text-center text-xs text-gray-400">
              {t("emptyTree")}
            </div>
          ) : (
            <FileTree
              tree={tree}
              selectedPath={selectedTreePath}
              onSelect={handleSelectTreePath}
            />
          )}
        </GlassCard>

        {/* Content pane */}
        <GlassCard className="flex min-w-0 flex-1 flex-col overflow-hidden !p-0">
          {mode === "new" ? (
            <DocumentEditor
              projectId={projectId}
              document={null}
              onSaved={handleSaved}
              onCancel={() => setMode("view")}
            />
          ) : mode === "edit" && detail && !isFileDetail ? (
            <DocumentEditor
              projectId={projectId}
              document={detail}
              onSaved={handleSaved}
              onCancel={() => setMode("view")}
            />
          ) : isLoadingDetail ? (
            <div className="flex flex-1 items-center justify-center">
              <div className="flex items-center gap-1">
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:0ms]" />
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:150ms]" />
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:300ms]" />
              </div>
            </div>
          ) : !detail ? (
            <div className="flex flex-1 items-center justify-center p-8">
              <EmptyState
                title={documents.length === 0 ? t("empty") : t("selectDocument")}
                description={documents.length === 0 ? t("emptyDescription") : undefined}
              />
            </div>
          ) : (
            <div className="relative flex min-h-0 flex-1">
              {/* Document viewer */}
              <div className="flex min-w-0 flex-1 flex-col">
                <div className="flex items-center justify-between gap-3 border-b border-white/[0.04] px-5 py-3">
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <h2 className="truncate font-display text-base font-semibold text-white">
                        {viewingRevision ? viewingRevision.title : detail.title}
                      </h2>
                      <Badge variant={detail.source === "Manual" ? "default" : "info"}>
                        {detail.source === "Agent"
                          ? t("sourceAgent")
                          : detail.source === "Brief"
                            ? t("sourceBrief")
                            : detail.source === "Task"
                              ? t("sourceTask")
                              : t("sourceManual")}
                      </Badge>
                    </div>
                    <p className="mt-0.5 truncate text-xs text-gray-400">
                      {viewingRevision
                        ? t("viewingVersion", { version: viewingRevision.version })
                        : `${detail.path || "/"} · v${detail.currentVersion} · ${formatRelativeTime(detail.updatedAt)}`}
                    </p>
                  </div>
                  <div className="flex shrink-0 flex-wrap items-center justify-end gap-1.5">
                    {viewingRevision ? (
                      <>
                        <Button
                          size="sm"
                          variant="ghost"
                          onClick={() => {
                            setViewingRevision(null);
                            setShowDiff(false);
                            setDiffBase(null);
                          }}
                        >
                          {t("backToCurrent")}
                        </Button>
                        {/* Nothing to diff on a file document — its revisions only ever
                            differ by title and folder, and both are already on screen. */}
                        {viewingRevision.version > 1 && !isFileDetail && (
                          <Button
                            size="sm"
                            variant="ghost"
                            onClick={handleToggleDiff}
                            className={cn(showDiff && "!bg-white/[0.08] !text-white")}
                          >
                            {t("diff")}
                          </Button>
                        )}
                        {viewingRevision.version !== detail.currentVersion && (
                          <RequirePermission permission="DocumentsManage">
                            <Button
                              size="sm"
                              onClick={() => handleRestoreRevision(viewingRevision.version)}
                              disabled={isRestoring}
                            >
                              {isRestoring ? t("restoring") : t("restore")}
                            </Button>
                          </RequirePermission>
                        )}
                      </>
                    ) : (
                      <>
                        {/* A file document has no markdown body to edit or convert — the
                            only meaningful action is getting the original file back. */}
                        {!isFileDetail && (
                          <RequirePermission permission="DocumentsManage">
                            <Button size="sm" variant="ghost" onClick={() => setMode("edit")}>
                              {t("edit")}
                            </Button>
                          </RequirePermission>
                        )}
                        {isFileDetail ? (
                          <Button
                            size="sm"
                            variant="ghost"
                            disabled={isDownloading}
                            onClick={handleDownloadFile}
                          >
                            {isDownloading ? t("downloading") : t("download")}
                          </Button>
                        ) : (
                          <Dropdown
                            align="right"
                            trigger={
                              <Button size="sm" variant="ghost" disabled={isDownloading}>
                                {isDownloading ? t("downloading") : t("export")}
                              </Button>
                            }
                            items={[
                              { key: "md", label: t("exportMarkdown") },
                              { key: "pdf", label: t("exportPdf") },
                              { key: "docx", label: t("exportWord") },
                            ]}
                            onSelect={handleExport}
                          />
                        )}
                        <Button
                          size="sm"
                          variant="ghost"
                          onClick={() => setShowHistory((v) => !v)}
                          className={cn(showHistory && "!bg-white/[0.08] !text-white")}
                        >
                          {t("history")}
                        </Button>
                        <RequirePermission permission="DocumentsManage">
                          <Button
                            size="sm"
                            variant="ghost"
                            onClick={() => setConfirmingDelete(true)}
                            className="!text-red-400 hover:!bg-red-500/10"
                          >
                            {t("delete")}
                          </Button>
                        </RequirePermission>
                      </>
                    )}
                  </div>
                </div>
                <div className="flex min-h-0 flex-1 flex-col overflow-y-auto px-5 py-4">
                  {detail.fileName ? (
                    // The uploaded file IS the document — shown by a viewer, never decoded
                    // into markdown. Old revisions point at the same file: only the title
                    // and folder ever changed.
                    <PdfViewer
                      load={() =>
                        withToken((token) => api.documents.getFile(projectId, detail.id, token))
                      }
                      sourceKey={detail.id}
                      fileName={detail.fileName}
                      sizeBytes={detail.fileSizeBytes}
                      labels={{
                        loading: t("filePreviewLoading"),
                        failed: t("filePreviewFailed"),
                        open: t("openFile"),
                        download: t("download"),
                        noInlineViewer: t("filePreviewUnsupported"),
                      }}
                    />
                  ) : viewingRevision && showDiff && diffBase ? (
                    <DocumentDiffView
                      oldContent={diffBase.content}
                      newContent={viewingRevision.content}
                    />
                  ) : (
                    <Markdown>{viewingRevision ? viewingRevision.content : detail.content}</Markdown>
                  )}
                </div>
              </div>

              {/* History panel */}
              {showHistory && (
                <div className="absolute inset-y-0 right-0 z-10 w-full max-w-xs shrink-0 overflow-y-auto border-l border-white/[0.04] bg-gray-950/95 p-3 backdrop-blur-xl lg:static lg:w-64 lg:max-w-none lg:bg-transparent lg:backdrop-blur-none">
                  <div className="mb-2 flex items-center justify-between px-1">
                    <p className="text-label">
                      {t("history")}
                    </p>
                    <button
                      type="button"
                      onClick={() => setShowHistory(false)}
                      aria-label={t("history")}
                      className="rounded p-1 text-gray-400 transition-colors hover:text-gray-300 lg:hidden"
                    >
                      <svg width="12" height="12" viewBox="0 0 16 16" fill="none">
                        <path d="M12 4L4 12M4 4l8 8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                      </svg>
                    </button>
                  </div>
                  {revisions.length === 0 ? (
                    <p className="px-1 text-xs text-gray-400">{t("noRevisions")}</p>
                  ) : (
                    <div className="space-y-1">
                      {revisions.map((rev) => (
                        <div
                          key={rev.version}
                          role="button"
                          tabIndex={0}
                          onClick={() => handleViewRevision(rev.version)}
                          onKeyDown={(e) => {
                            if (e.key === "Enter" || e.key === " ") handleViewRevision(rev.version);
                          }}
                          className={cn(
                            "w-full cursor-pointer rounded-lg px-2.5 py-2 text-left transition-colors",
                            viewingRevision?.version === rev.version
                              ? "bg-white/[0.08]"
                              : "hover:bg-white/[0.04]"
                          )}
                        >
                          <div className="flex items-center justify-between">
                            <span className="text-xs font-medium text-gray-200">
                              v{rev.version}
                            </span>
                            {rev.version === detail.currentVersion && (
                              <span className="text-xs text-emerald-400">
                                {t("currentVersion")}
                              </span>
                            )}
                          </div>
                          <p className="mt-0.5 text-xs text-gray-400">
                            {rev.changedByAgentName ??
                              rev.changedByUserName ??
                              (rev.changedByAgentId ? t("agentAuthor") : t("userAuthor"))}{" "}
                            · {formatRelativeTime(rev.createdAt)}
                          </p>
                          {rev.changedByAgentId && rev.requestedByUserName && (
                            <p className="mt-0.5 text-xs text-gray-400">
                              {t("askedBy", { name: rev.requestedByUserName })}
                            </p>
                          )}
                          {rev.sourceTaskId && (
                            <Link
                              href={`/p/${projectId}/tasks/${rev.sourceTaskId}`}
                              onClick={(e) => e.stopPropagation()}
                              className="mt-1 inline-flex items-center gap-1 text-xs text-primary-400 transition-colors hover:text-primary-300"
                            >
                              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <path d="M9 11l3 3L22 4M21 12v7a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2h11" strokeLinecap="round" strokeLinejoin="round" />
                              </svg>
                              {t("fromTask", { title: rev.sourceTaskTitle ?? t("deletedTask") })}
                            </Link>
                          )}
                          {rev.changedByAgentId && rev.sourceConversationId && (
                            <Link
                              href={`/p/${projectId}/team?agent=${rev.changedByAgentId}&tab=chat&c=${rev.sourceConversationId}${rev.sourceMessageId ? `&m=${rev.sourceMessageId}` : ""}`}
                              onClick={(e) => e.stopPropagation()}
                              className="mt-1 inline-flex items-center gap-1 text-xs text-primary-400 transition-colors hover:text-primary-300"
                            >
                              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <path d="M21 15a2 2 0 01-2 2H7l-4 4V5a2 2 0 012-2h14a2 2 0 012 2z" strokeLinecap="round" strokeLinejoin="round" />
                              </svg>
                              {t("viewInChat")}
                            </Link>
                          )}
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          )}
        </GlassCard>
      </div>

      <ConfirmDialog
        open={confirmingDelete && detail !== null}
        title={detail?.title ?? ""}
        message={t("confirmDelete")}
        confirmLabel={t("delete")}
        cancelLabel={t("cancel")}
        danger
        onConfirm={handleDelete}
        onCancel={() => setConfirmingDelete(false)}
      />

      {/* Rename a folder (bulk path-prefix move on the server) */}
      {renameFolderOpen && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
          onClick={() => setRenameFolderOpen(false)}
        >
          <div
            className="w-full max-w-sm rounded-2xl border border-white/[0.08] bg-surface-overlay p-4 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <h3 className="mb-3 font-display text-sm font-semibold text-white">
              {tFolders("renameFolder")}
            </h3>
            <div className="flex flex-col gap-2">
              <Select
                size="sm"
                aria-label={tFolders("renameFolder")}
                value={renameFrom}
                onChange={(e) => setRenameFrom(e.target.value)}
              >
                {folderPaths.map((path) => (
                  <option key={path} value={path}>
                    {path}
                  </option>
                ))}
              </Select>
              <input
                value={renameTo}
                onChange={(e) => setRenameTo(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && renameTo.trim()) handleRenameFolder();
                  if (e.key === "Escape") setRenameFolderOpen(false);
                }}
                placeholder={tFolders("newFolderPlaceholder")}
                className="rounded-lg border border-white/[0.08] bg-white/[0.03] px-2 py-1.5 text-xs text-white placeholder-gray-600 outline-none focus:border-primary-500/50"
              />
              <button
                disabled={!renameTo.trim() || renameTo.trim() === renameFrom}
                onClick={handleRenameFolder}
                className="rounded-lg bg-primary-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-primary-500 disabled:opacity-40"
              >
                {tFolders("renameFolder")}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

export { DocumentsBrowser };
