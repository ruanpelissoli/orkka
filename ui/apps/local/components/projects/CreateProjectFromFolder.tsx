import { useState } from "react";
import { useTranslations } from "use-intl";
import { useAuth } from "@/lib/local-auth-client";
import { Badge, Button, Input } from "@/components/ui";
import { api, ApiError, extractErrorMessage } from "@/lib/api";
import type {
  CreateProjectFromFolderResponse,
  DiscoveredRepo,
  DiscoverReposResponse,
} from "@/lib/types";
import {
  LocalFolderPickerModal,
  type PickedFolder,
} from "@/components/repos/LocalFolderPickerModal";

type CreatedProject = CreateProjectFromFolderResponse["project"];

/** The server's own words for a failed call, or null when it gave none worth showing. */
function serverMessage(error: unknown): string | null {
  if (error instanceof ApiError) {
    const message = extractErrorMessage(error, "");
    return message || null;
  }
  return null;
}

/**
 * Create a project from one folder on this machine: pick it, preview what the runner finds
 * in it (one repository, or a folder of repositories), name the project, create it.
 *
 * Nothing is written while previewing — discovery only reads the folder.
 */
export function CreateProjectFromFolder({
  onCreated,
}: {
  onCreated: (project: CreatedProject) => void;
}) {
  const t = useTranslations("dashboard.fromFolder");
  const tPicker = useTranslations("dashboard.folderPicker");
  const { getToken } = useAuth();

  const [pickerOpen, setPickerOpen] = useState(false);
  const [folder, setFolder] = useState<PickedFolder | null>(null);
  const [discovery, setDiscovery] = useState<DiscoverReposResponse | null>(null);
  const [discovering, setDiscovering] = useState(false);
  /** Set when discovery threw; `discoverDetail` holds the server's words when it gave any. */
  const [discoverFailed, setDiscoverFailed] = useState(false);
  const [discoverDetail, setDiscoverDetail] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [nameTouched, setNameTouched] = useState(false);

  const [creating, setCreating] = useState(false);
  const [createFailed, setCreateFailed] = useState(false);
  const [createDetail, setCreateDetail] = useState<string | null>(null);
  /** Set when the project was created with warnings worth reading before moving on. */
  const [created, setCreated] = useState<CreateProjectFromFolderResponse | null>(null);

  async function discover(picked: PickedFolder) {
    setFolder(picked);
    if (!nameTouched) setName(picked.name);
    setDiscovery(null);
    setDiscoverFailed(false);
    setDiscoverDetail(null);
    setCreateFailed(false);
    setCreateDetail(null);
    setDiscovering(true);
    try {
      const token = await getToken();
      setDiscovery(await api.runnerFs.discover(picked.fullPath, token));
    } catch (error) {
      setDiscoverFailed(true);
      setDiscoverDetail(serverMessage(error));
    } finally {
      setDiscovering(false);
    }
  }

  async function handleCreate() {
    if (!folder || creating) return;
    setCreating(true);
    setCreateFailed(false);
    setCreateDetail(null);
    try {
      const token = await getToken();
      const result = await api.projects.createFromFolder(
        { path: folder.fullPath, name: name.trim() || undefined },
        token
      );
      if (result.warnings.length > 0) {
        setCreated(result);
        setCreating(false);
        return;
      }
      onCreated(result.project);
    } catch (error) {
      setCreateFailed(true);
      setCreateDetail(serverMessage(error));
      setCreating(false);
    }
  }

  // Created with notes: say them once, here, then let the user move on.
  if (created) {
    return (
      <div className="space-y-4" data-testid="from-folder-created">
        <p className="text-sm text-gray-200">
          {t("createdWithWarnings", { name: created.project.name })}
        </p>
        <ul className="space-y-1.5">
          {created.warnings.map((warning) => (
            <li
              key={warning}
              className="rounded-lg border border-amber-500/20 bg-amber-500/10 px-3 py-2 text-sm text-amber-400"
            >
              {warning}
            </li>
          ))}
        </ul>
        <div className="flex justify-end">
          <Button onClick={() => onCreated(created.project)}>{t("openProject")}</Button>
        </div>
      </div>
    );
  }

  const offline = discovery !== null && !discovery.supported;
  const readError = discovery?.supported && discovery.error ? discovery.error : null;
  const repos = discovery?.supported && !discovery.error ? discovery.repos : [];
  const usable = repos.filter((r) => r.hasCommits);
  const canCreate = !!folder && usable.length > 0 && !discovering && !creating;

  return (
    <div className="space-y-6">
      {/* 1. The folder */}
      <section>
        <h2 className="font-display text-sm font-semibold text-white">{t("folderTitle")}</h2>
        <p className="mt-1 mb-3 text-xs text-gray-400">{t("folderHint")}</p>
        <div className="flex flex-wrap items-center gap-3 rounded-lg border border-white/[0.06] bg-white/[0.02] p-3">
          <svg
            width="16"
            height="16"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            className="shrink-0 text-primary-400"
          >
            <path
              d="M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2z"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
          <span
            className="min-w-0 flex-1 truncate font-mono text-xs text-gray-300"
            data-testid="from-folder-path"
          >
            {folder ? folder.fullPath : t("noFolder")}
          </span>
          <Button size="sm" variant={folder ? "secondary" : "primary"} onClick={() => setPickerOpen(true)}>
            {folder ? t("changeFolder") : t("chooseFolder")}
          </Button>
        </div>
      </section>

      {/* 2. What is in it */}
      {folder && (
        <section aria-live="polite">
          {discovering && <p className="text-sm text-gray-400">{t("discovering")}</p>}

          {offline && (
            <div className="rounded-xl border border-amber-500/20 bg-amber-500/10 px-4 py-3 text-sm text-amber-400">
              {t("runnerOffline")}
            </div>
          )}

          {(discoverFailed || readError) && (
            <div className="rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-sm text-red-400">
              {t("readFailed", {
                detail: readError ?? discoverDetail ?? tPicker("apiUnreachable"),
              })}
            </div>
          )}

          {discovery?.supported && !discovery.error && (
            <>
              {repos.length === 0 ? (
                <p className="rounded-lg border border-amber-500/20 bg-amber-500/10 px-3 py-2 text-sm text-amber-400">
                  {t("noRepos")}
                </p>
              ) : (
                <>
                  <div className="mb-2 flex items-center gap-2">
                    <h2 className="font-display text-sm font-semibold text-white">
                      {t("foundTitle")}
                    </h2>
                    <Badge variant="primary" size="sm" data-testid="from-folder-shape">
                      {discovery.shape === "monorepo"
                        ? t("shapeMonorepo")
                        : t("shapeMultiRepo", { count: repos.length })}
                    </Badge>
                  </div>
                  <ul className="space-y-2" data-testid="from-folder-repos">
                    {repos.map((repo) => (
                      <DiscoveredRepoRow key={repo.relativePath || "."} repo={repo} />
                    ))}
                  </ul>
                </>
              )}
            </>
          )}
        </section>
      )}

      {/* 3. Name it, create it */}
      {folder && usable.length > 0 && (
        <section>
          <Input
            label={t("nameLabel")}
            hint={t("nameHint")}
            value={name}
            maxLength={200}
            onChange={(e) => {
              setNameTouched(true);
              setName(e.target.value);
            }}
          />
        </section>
      )}

      {createFailed && (
        <div
          role="alert"
          className="rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-sm text-red-400"
        >
          {t("createFailed", { detail: createDetail ?? tPicker("apiUnreachable") })}
        </div>
      )}

      <div className="flex justify-end">
        <Button onClick={handleCreate} disabled={!canCreate}>
          {creating ? (
            <>
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-white/20 border-t-white" />
              {t("creating")}
            </>
          ) : (
            t("create")
          )}
        </Button>
      </div>

      <LocalFolderPickerModal
        open={pickerOpen}
        onClose={() => setPickerOpen(false)}
        onPick={(picked) => void discover(picked)}
      />
    </div>
  );
}

function DiscoveredRepoRow({ repo }: { repo: DiscoveredRepo }) {
  const t = useTranslations("dashboard.fromFolder");
  const branch = repo.currentBranch ?? repo.defaultBranch;

  return (
    <li
      className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-3 py-2.5"
      data-testid="from-folder-repo"
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-sm font-medium text-white">{repo.name}</span>
        <span className="truncate font-mono text-xs text-gray-400">
          {repo.relativePath || t("rootPath")}
        </span>
        {repo.location === "remote" ? (
          <Badge variant="info" size="sm" title={repo.originUrl ?? undefined}>
            {repo.host ? t("remoteAt", { host: repo.host }) : t("remote")}
          </Badge>
        ) : (
          <Badge variant="default" size="sm">
            {t("localFolder")}
          </Badge>
        )}
        {branch && (
          <span className="font-mono text-xs text-gray-400">{t("onBranch", { branch })}</span>
        )}
        {repo.stack && (
          <Badge variant="purple" size="sm">
            {repo.stack}
          </Badge>
        )}
      </div>

      {repo.description && <p className="mt-1 text-xs text-gray-400">{repo.description}</p>}

      {(!repo.hasCommits || repo.isDirty || repo.hasUnpushedCommits) && (
        <div className="mt-1.5 flex flex-wrap gap-1.5">
          {!repo.hasCommits && (
            <Badge variant="error" size="sm">
              {t("noCommits")}
            </Badge>
          )}
          {repo.isDirty && (
            <Badge variant="warning" size="sm">
              {t("dirty")}
            </Badge>
          )}
          {repo.hasUnpushedCommits && (
            <Badge variant="warning" size="sm">
              {t("unpushed")}
            </Badge>
          )}
        </div>
      )}
    </li>
  );
}
