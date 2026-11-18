import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useTranslations } from "use-intl";
import { Modal, Button, Badge, Skeleton } from "@/components/ui";
import { api } from "@/lib/api";
import type { DirectoryEntry } from "@/lib/types";
import { cn } from "@/lib/utils";

/**
 * Folder picker for the user's own machine.
 *
 * This browser cannot see the filesystem it is showing. Every listing travels API → the Worker pod
 * holding this user's runner socket → the runner, so an unavailable runner is a first-class
 * state rather than an error — there is nothing the user can do about it from inside a web
 * page except start their runner.
 *
 * Note on state: failures are stored as the SERVER's message (or null for "we never got
 * there"), never as translated text. Translating at render keeps the translator out of the
 * fetch callbacks' dependencies — `useTranslations` does not return a stable identity, and a
 * `t` in the dependency array of the callback an effect depends on is an infinite loop.
 */

type LinkStatus = "pending" | "linking" | "ok" | "error";

interface LinkResult {
  fullPath: string;
  name: string;
  status: LinkStatus;
  /** The server's own words. Null means the request never produced any. */
  detail: string | null;
  /** What the import left out of the repository, when it made one. */
  excluded: string[];
  /** Where the folder was copied first, when a copy was made. */
  snapshot: string | null;
}

/** The server's message, or null when the failure produced none worth showing. */
function serverMessage(error: unknown): string | null {
  return error instanceof Error && error.message ? error.message : null;
}

export interface PickedFolder {
  fullPath: string;
  name: string;
}

interface LocalFolderPickerModalProps {
  open: boolean;
  onClose: () => void;
  /** Link mode: the chosen folders are linked to this project on confirm. */
  projectId?: string;
  /** Called after the link batch so the caller can refresh its repo list. */
  onLinked?: () => void;
  /**
   * Pick mode, for when the project does not exist yet (creating one from a folder). One
   * folder is chosen — a selected entry, else the folder being shown — and handed back
   * instead of linked. Takes precedence over link mode.
   */
  onPick?: (folder: PickedFolder) => void;
}

/** The last path segment, for naming a folder the user navigated into. */
function folderName(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, "");
  const parts = trimmed.split(/[\\/]/);
  return parts[parts.length - 1] || trimmed;
}

export function LocalFolderPickerModal({
  open,
  onClose,
  projectId,
  onLinked,
  onPick,
}: LocalFolderPickerModalProps) {
  const t = useTranslations("dashboard");
  const tCommon = useTranslations("common");
  const { getToken } = useAuth();

  const [currentPath, setCurrentPath] = useState<string | null>(null);
  const [parentPath, setParentPath] = useState<string | null>(null);
  const [entries, setEntries] = useState<DirectoryEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [browseError, setBrowseError] = useState<string | null>(null);
  const [browseFailed, setBrowseFailed] = useState(false);
  const [runnerUnavailable, setRunnerUnavailable] = useState(false);

  const [selected, setSelected] = useState<Map<string, string>>(new Map());
  const [linking, setLinking] = useState(false);
  const [linkResults, setLinkResults] = useState<LinkResult[]>([]);

  // "New repository" — a folder + git init inside the directory currently shown.
  const [showCreate, setShowCreate] = useState(false);
  const [newRepoName, setNewRepoName] = useState("");
  const [creatingRepo, setCreatingRepo] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [createFailed, setCreateFailed] = useState(false);
  const [createdButUnlinked, setCreatedButUnlinked] = useState(false);

  // The synchronous half of a browse: show the loading state and clear the last outcome.
  const beginBrowse = () => {
    setLoading(true);
    setBrowseError(null);
    setBrowseFailed(false);
    setRunnerUnavailable(false);
  };

  // The asynchronous half: fetch the directory and show what came back. Every state update
  // happens in a promise callback, so the open effect below can start it.
  const loadDirectory = useCallback(
    (path: string | null): Promise<void> =>
      getToken()
        .then((token) => api.runnerFs.browse(path, token))
        .then(
          (data) => {
            if (!data.supported) {
              setRunnerUnavailable(true);
              setEntries([]);
              return;
            }

            setCurrentPath(data.path);
            setParentPath(data.parentPath);
            setEntries(data.entries ?? []);
            if (data.error) setBrowseError(data.error);
          },
          (error: unknown) => {
            setBrowseError(serverMessage(error));
            setBrowseFailed(true);
            setEntries([]);
          }
        )
        .finally(() => {
          setLoading(false);
        }),
    [getToken]
  );

  const browse = (path: string | null) => {
    beginBrowse();
    return loadDirectory(path);
  };

  // Reset and load the roots each time the modal opens: the reset is adjusted during
  // render, the load runs in the effect.
  const [prevOpen, setPrevOpen] = useState(false);
  if (open !== prevOpen) {
    setPrevOpen(open);
    if (open) {
      setSelected(new Map());
      setLinkResults([]);
      setCurrentPath(null);
      setParentPath(null);
      setShowCreate(false);
      setNewRepoName("");
      setCreateError(null);
      setCreateFailed(false);
      setCreatedButUnlinked(false);
      beginBrowse();
    }
  }

  useEffect(() => {
    if (open) void loadDirectory(null);
  }, [open, loadDirectory]);

  const linkOne = useCallback(
    async (
      path: string
    ): Promise<{
      ok: boolean;
      detail: string | null;
      excluded: string[];
      snapshot: string | null;
    }> => {
      const nothing = { detail: null, excluded: [], snapshot: null };
      try {
        const token = await getToken();
        if (!projectId) return { ok: false, ...nothing };
        const linked = await api.repos.linkLocal(projectId, path, token);
        return {
          ok: true,
          detail: null,
          excluded: linked.excludedPaths ?? [],
          snapshot: linked.snapshotPath ?? null,
        };
      } catch (error) {
        return { ok: false, ...nothing, detail: serverMessage(error) };
      }
    },
    [getToken, projectId]
  );

  const handleCreateRepo = useCallback(async () => {
    const name = newRepoName.trim();
    if (!name || !currentPath || creatingRepo) return;

    setCreatingRepo(true);
    setCreateError(null);
    setCreateFailed(false);
    setCreatedButUnlinked(false);
    try {
      const token = await getToken();

      const created = await api.runnerFs.initRepo(currentPath, name, token);

      const linked = await linkOne(created.path);
      if (!linked.ok) {
        // The folder EXISTS now — saying only "failed" would send the user looking for
        // something to retry when what they need is to link what is already on disk.
        setCreatedButUnlinked(true);
        setCreateError(linked.detail);
        return;
      }
      onLinked?.();
      onClose();
    } catch (error) {
      setCreateError(serverMessage(error));
      setCreateFailed(true);
    } finally {
      setCreatingRepo(false);
    }
  }, [
    newRepoName,
    currentPath,
    creatingRepo,
    getToken,
    linkOne,
    onLinked,
    onClose,
  ]);

  const pickMode = !!onPick;

  const toggleSelect = useCallback(
    (entry: DirectoryEntry) => {
      setSelected((prev) => {
        // Pick mode chooses ONE folder: selecting another replaces it.
        const next = pickMode ? new Map<string, string>() : new Map(prev);
        if (prev.has(entry.fullPath)) next.delete(entry.fullPath);
        else next.set(entry.fullPath, entry.name);
        return next;
      });
    },
    [pickMode]
  );

  // Pick mode: the selected entry, else the folder being shown (the drive list is not a folder).
  const firstSelected = selected.entries().next();
  const pickTarget: PickedFolder | null = !firstSelected.done
    ? { fullPath: firstSelected.value[0], name: firstSelected.value[1] }
    : currentPath
      ? { fullPath: currentPath, name: folderName(currentPath) }
      : null;

  const handlePick = () => {
    if (!onPick || !pickTarget) return;
    onPick(pickTarget);
    onClose();
  };

  const handleLink = useCallback(async () => {
    if (selected.size === 0 || linking) return;
    if (!projectId) return;
    setLinking(true);

    const queue: LinkResult[] = Array.from(selected.entries()).map(
      ([fullPath, name]) => ({
        fullPath,
        name,
        status: "pending" as LinkStatus,
        detail: null,
        excluded: [],
        snapshot: null,
      })
    );
    setLinkResults(queue);

    let allOk = true;
    for (const item of queue) {
      setLinkResults((prev) =>
        prev.map((r) =>
          r.fullPath === item.fullPath ? { ...r, status: "linking" } : r
        )
      );

      const result = await linkOne(item.fullPath);
      allOk = allOk && result.ok;

      setLinkResults((prev) =>
        prev.map((r) =>
          r.fullPath === item.fullPath
            ? result.ok
              ? {
                  ...r,
                  status: "ok",
                  detail: null,
                  excluded: result.excluded,
                  snapshot: result.snapshot,
                }
              : { ...r, status: "error", detail: result.detail }
            : r
        )
      );

      if (result.ok) {
        setSelected((prev) => {
          const next = new Map(prev);
          next.delete(item.fullPath);
          return next;
        });
      }
    }

    setLinking(false);
    onLinked?.();
    if (allOk) onClose();
  }, [selected, linking, projectId, linkOne, onLinked, onClose]);

  return (
    <Modal
      open={open}
      onClose={linking ? () => {} : onClose}
      title={onPick ? t("folderPicker.pickTitle") : t("folderPicker.linkTitle")}
      className="md:max-w-2xl"
    >
      <div className="flex flex-col gap-3">
        {/* Where we are */}
        <div className="flex items-center gap-2 rounded-xl border border-white/[0.06] bg-white/[0.03] px-3 py-2">
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            className="shrink-0 text-gray-400"
          >
            <path
              d="M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2z"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
          <span className="truncate font-mono text-xs text-gray-300">
            {currentPath ?? t("folderPicker.thisMachine")}
          </span>
        </div>

        {/* No machine can answer — the one state a browser cannot resolve on its own. */}
        {runnerUnavailable && (
          <div className="rounded-xl border border-amber-500/20 bg-amber-500/10 px-4 py-3">
            <p className="text-sm text-amber-400">
              {t("folderPicker.runnerOffline")}
            </p>
          </div>
        )}

        {(browseError || browseFailed) && !runnerUnavailable && (
          <div className="rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2">
            <p className="text-sm text-red-400">
              {browseError ?? t("folderPicker.apiUnreachable")}
            </p>
          </div>
        )}

        {/* Listing — shrinks on short viewports so the footer stays inside the modal */}
        <div className="h-[min(18rem,100dvh_-_24rem)] min-h-32 overflow-y-auto rounded-xl border border-white/[0.06] bg-white/[0.02]">
          {loading ? (
            <div className="space-y-2 p-3">
              <Skeleton className="h-8 w-full" />
              <Skeleton className="h-8 w-5/6" />
              <Skeleton className="h-8 w-3/4" />
              <Skeleton className="h-8 w-4/5" />
            </div>
          ) : (
            <div className="p-1.5">
              {currentPath !== null && (
                <button
                  type="button"
                  onClick={() => browse(parentPath)}
                  className="flex w-full items-center gap-2.5 rounded-lg px-3 py-2 text-left text-sm text-gray-400 transition-colors hover:bg-white/[0.04] hover:text-gray-200"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    className="shrink-0"
                  >
                    <path d="M9 14L4 9l5-5" strokeLinecap="round" strokeLinejoin="round" />
                    <path
                      d="M4 9h10.5a5.5 5.5 0 015.5 5.5v0a5.5 5.5 0 01-5.5 5.5H11"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                    />
                  </svg>
                  <span className="font-mono">..</span>
                </button>
              )}

              {entries.length === 0 &&
                !runnerUnavailable &&
                !browseError &&
                !browseFailed && (
                  <p className="px-3 py-6 text-center text-sm text-gray-400">
                    {t("folderPicker.noFolders")}
                  </p>
                )}

              {entries.map((entry) => {
                const isSelected = selected.has(entry.fullPath);
                return (
                  <div
                    key={entry.fullPath}
                    className={cn(
                      "group flex w-full items-center gap-2.5 rounded-lg px-3 py-2 text-sm transition-colors",
                      isSelected
                        ? "bg-primary-500/10 text-white"
                        : "text-gray-300 hover:bg-white/[0.04] hover:text-gray-100"
                    )}
                  >
                    <button
                      type="button"
                      onClick={() => toggleSelect(entry)}
                      className="flex min-w-0 flex-1 items-center gap-2.5 text-left"
                    >
                      <span
                        className={cn(
                          "flex h-4 w-4 shrink-0 items-center justify-center rounded border transition-colors",
                          isSelected
                            ? "border-primary-500 bg-primary-500 text-white"
                            : "border-white/[0.15] bg-white/[0.03]"
                        )}
                      >
                        {isSelected && (
                          <svg
                            width="10"
                            height="10"
                            viewBox="0 0 16 16"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2.5"
                          >
                            <path
                              d="M3 8.5l3.5 3.5L13 4.5"
                              strokeLinecap="round"
                              strokeLinejoin="round"
                            />
                          </svg>
                        )}
                      </span>
                      <span className="truncate">{entry.name}</span>
                      {entry.isGitRepo && (
                        <Badge variant="info" size="sm">
                          git
                        </Badge>
                      )}
                    </button>

                    <button
                      type="button"
                      onClick={() => browse(entry.fullPath)}
                      title={t("folderPicker.openFolder", { name: entry.name })}
                      className="shrink-0 rounded-md p-1 text-gray-400 opacity-0 transition-colors group-hover:opacity-100 hover:bg-white/[0.06] hover:text-gray-200"
                    >
                      <svg
                        width="14"
                        height="14"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="2"
                      >
                        <path d="M9 18l6-6-6-6" strokeLinecap="round" strokeLinejoin="round" />
                      </svg>
                    </button>
                  </div>
                );
              })}
            </div>
          )}
        </div>

        {/* Start something new, right here. Not in pick mode: a project is created from a
            folder that already holds committed work, and an empty repository has none. */}
        {!pickMode && (
        <div className="rounded-xl border border-white/[0.06] bg-white/[0.02]">
          {!showCreate ? (
            <button
              type="button"
              onClick={() => setShowCreate(true)}
              disabled={runnerUnavailable}
              className="flex w-full items-center gap-2 px-3 py-2 text-left text-xs text-gray-400 transition-colors hover:text-gray-200 disabled:opacity-50"
            >
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
              </svg>
              {t("folderPicker.newRepo")}
            </button>
          ) : (
            <div className="space-y-2 p-3">
              <p className="text-xs text-gray-400">
                {currentPath
                  ? t.rich("folderPicker.createIn", {
                      path: currentPath,
                      mono: (chunks) => (
                        <span className="font-mono text-gray-400">{chunks}</span>
                      ),
                    })
                  : t("folderPicker.navigateFirst")}
              </p>
              <div className="flex gap-2">
                <input
                  type="text"
                  value={newRepoName}
                  onChange={(e) => setNewRepoName(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") {
                      e.preventDefault();
                      handleCreateRepo();
                    }
                  }}
                  placeholder="my-new-repo"
                  disabled={!currentPath || creatingRepo}
                  className="flex-1 rounded-lg border border-white/[0.08] bg-white/[0.03] px-3 py-1.5 font-mono text-xs text-gray-200 placeholder:text-gray-500 focus:border-primary-500/50 focus:outline-none disabled:opacity-50"
                />
                <Button
                  size="sm"
                  disabled={!currentPath || !newRepoName.trim() || creatingRepo}
                  onClick={handleCreateRepo}
                >
                  {creatingRepo
                    ? t("folderPicker.creating")
                    : t("folderPicker.createAndLink")}
                </Button>
              </div>
              {(createError || createFailed || createdButUnlinked) && (
                <p className="text-xs text-red-400">
                  {createdButUnlinked
                    ? t("folderPicker.createdButLinkFailed", {
                        detail: createError ?? t("folderPicker.apiUnreachable"),
                      })
                    : (createError ?? t("folderPicker.apiUnreachable"))}
                </p>
              )}
            </div>
          )}
        </div>
        )}

        {/* Per-folder link progress */}
        {linkResults.length > 0 && (
          <div className="max-h-36 space-y-1 overflow-y-auto">
            {linkResults.map((r) => (
              <div
                key={r.fullPath}
                className="flex items-start gap-2 rounded-lg border border-white/[0.04] bg-white/[0.02] px-3 py-1.5"
              >
                <span className="mt-0.5 shrink-0">
                  {r.status === "ok" ? (
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="text-emerald-400">
                      <path d="M20 6L9 17l-5-5" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  ) : r.status === "error" ? (
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="text-red-400">
                      <path d="M18 6L6 18M6 6l12 12" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  ) : (
                    <svg
                      width="14"
                      height="14"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="2"
                      className={cn(
                        "text-gray-400",
                        r.status === "linking" && "animate-spin text-primary-400"
                      )}
                    >
                      <path d="M21 12a9 9 0 11-6.22-8.56" strokeLinecap="round" />
                    </svg>
                  )}
                </span>
                <div className="min-w-0 flex-1">
                  <p className="truncate font-mono text-xs text-gray-300">{r.name}</p>
                  {r.status === "error" && (
                    <p className="text-xs text-red-400">
                      {r.detail ?? t("folderPicker.apiUnreachable")}
                    </p>
                  )}
                  {/* Said here because this is the only moment it is still news. A file kept
                      out of the repository is still on disk, but it has no point in history
                      to come back to — and finding that out later, from an agent that cannot
                      see it, is the worst possible way to learn it. */}
                  {r.status === "ok" && r.excluded.length > 0 && (
                    <p className="text-xs text-gray-400">
                      {t("folderPicker.importExcluded", {
                        paths: r.excluded.join(", "),
                      })}
                    </p>
                  )}
                  {r.status === "ok" && r.snapshot && (
                    <p className="truncate text-xs text-gray-400">
                      {t("folderPicker.importSnapshot", { path: r.snapshot })}
                    </p>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}

        <div className="flex items-center justify-between pt-1">
          <span className="min-w-0 text-xs text-gray-400">
            {pickMode
              ? t("folderPicker.pickHint")
              : selected.size === 0
                ? t("folderPicker.selectAnyHint")
                : t("folderPicker.selectedCount", { count: selected.size })}
          </span>
          <div className="flex gap-3">
            <Button variant="secondary" size="md" onClick={onClose} disabled={linking}>
              {tCommon("actions.cancel")}
            </Button>
            {pickMode ? (
              <Button size="md" disabled={!pickTarget} onClick={handlePick}>
                {pickTarget
                  ? t("folderPicker.useFolder", { name: pickTarget.name })
                  : t("folderPicker.useThisFolder")}
              </Button>
            ) : (
              <Button
                size="md"
                disabled={selected.size === 0 || linking}
                onClick={handleLink}
              >
                {linking
                  ? t("folderPicker.linking")
                  : t("folderPicker.linkCount", { count: selected.size })}
              </Button>
            )}
          </div>
        </div>
      </div>
    </Modal>
  );
}
