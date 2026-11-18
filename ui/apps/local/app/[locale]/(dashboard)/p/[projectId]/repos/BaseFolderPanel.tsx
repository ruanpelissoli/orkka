import { useEffect, useRef, useState } from "react";
import { useFormatter, useTranslations } from "use-intl";
import { useAuth } from "@/lib/local-auth-client";
import { Badge, Button, GlassCard, Textarea } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { api, ApiError, extractErrorMessage, invalidateApiCache } from "@/lib/api";
import { isRemoteRepoUrl, repoUrlHost } from "@/lib/repo-url";
import type { Project, ProjectRepo, RescanProjectResponse, RescanRepoStatus } from "@/lib/types";
import { refreshData } from "@/lib/data-refresh";

/** True when a rescan changed which repositories the project has. */
function changedRepos(result: RescanProjectResponse): boolean {
  return (
    result.added.length + result.missing.length + result.restored.length + result.originUpdated.length >
    0
  );
}

/**
 * The top of the Repos page: the project's base folder with a Rescan button, and every
 * repository with its location, its working-copy state and an editable description.
 *
 * Opening the page sends one automatic rescan (`?auto=true`) — fire-and-forget: the server
 * throttles it and answers "skipped" when the runner is offline, and the page never waits
 * for it. Its result only adds badges, and refreshes the page when the repo set changed.
 */
export function BaseFolderPanel({
  projectId,
  project,
  repos,
}: {
  projectId: string;
  project: Pick<Project, "baseFolder" | "baseFolderShape"> | null;
  repos: ProjectRepo[];
}) {
  const t = useTranslations("dashboard.baseFolder");
  const tFolder = useTranslations("dashboard.fromFolder");
  const { getToken } = useAuth();

  const baseFolder = project?.baseFolder ?? null;
  const [statuses, setStatuses] = useState<Map<string, RescanRepoStatus>>(new Map());
  const [result, setResult] = useState<RescanProjectResponse | null>(null);
  const [rescanning, setRescanning] = useState(false);
  const [rescanFailed, setRescanFailed] = useState(false);
  const [rescanDetail, setRescanDetail] = useState<string | null>(null);

  function applyResult(next: RescanProjectResponse) {
    if (next.repos.length > 0) {
      setStatuses(new Map(next.repos.map((s) => [s.repoId, s])));
    }
    if (!next.skipped && changedRepos(next)) {
      invalidateApiCache();
      refreshData();
    }
  }

  // One automatic rescan per page open. A ref, not state: StrictMode's double effect must not
  // send two.
  const autoSent = useRef(false);
  useEffect(() => {
    if (!baseFolder || autoSent.current) return;
    autoSent.current = true;
    void (async () => {
      try {
        const token = await getToken();
        const auto = await api.projects.rescan(projectId, token, true);
        if (auto.offline) return;
        applyResult(auto);
        // Only worth saying when it found something; "nothing changed" is the quiet default.
        if (!auto.skipped && (changedRepos(auto) || auto.warnings.length > 0)) setResult(auto);
      } catch {
        // Silent by design: the automatic rescan is a convenience, the button is the real one.
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once per page open
  }, [baseFolder, projectId]);

  async function rescan() {
    if (rescanning) return;
    setRescanning(true);
    setRescanFailed(false);
    setRescanDetail(null);
    try {
      const token = await getToken();
      const next = await api.projects.rescan(projectId, token);
      setResult(next);
      applyResult(next);
    } catch (error) {
      setResult(null);
      setRescanFailed(true);
      setRescanDetail(error instanceof ApiError ? extractErrorMessage(error, "") || null : null);
    } finally {
      setRescanning(false);
    }
  }

  if (!baseFolder && repos.length === 0) return null;

  return (
    <GlassCard className="p-5" data-testid="base-folder-panel">
      {baseFolder && (
        <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="text-sm font-medium text-gray-100">{t("title")}</h2>
              {project?.baseFolderShape && (
                <Badge variant="primary" size="sm">
                  {project.baseFolderShape === "monorepo"
                    ? tFolder("shapeMonorepo")
                    : tFolder("shapeMultiRepo", { count: repos.length })}
                </Badge>
              )}
            </div>
            <p
              className="mt-1 truncate font-mono text-xs text-gray-300"
              data-testid="base-folder-path"
            >
              {baseFolder}
            </p>
            <p className="mt-1 text-xs text-gray-400">{t("rescanHint")}</p>
          </div>
          <RequirePermission permission="ReposManage">
            <Button size="sm" variant="secondary" onClick={rescan} disabled={rescanning}>
              {rescanning ? t("rescanning") : t("rescan")}
            </Button>
          </RequirePermission>
        </div>
      )}

      {rescanFailed && (
        <div
          role="alert"
          className="mb-4 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-sm text-red-400"
        >
          {t("rescanFailed", { detail: rescanDetail ?? t("apiUnreachable") })}
        </div>
      )}

      {result && <RescanSummary result={result} />}

      {repos.length > 0 && (
        <ul className="max-h-72 space-y-2 overflow-y-auto" data-testid="base-folder-repos">
          {repos.map((repo) => (
            <RepoRow
              key={repo.id}
              projectId={projectId}
              repo={repo}
              status={statuses.get(repo.id) ?? null}
            />
          ))}
        </ul>
      )}
    </GlassCard>
  );
}

function RescanSummary({ result }: { result: RescanProjectResponse }) {
  const t = useTranslations("dashboard.baseFolder");
  const lines: { key: string; text: string }[] = [];
  if (result.added.length) lines.push({ key: "added", text: t("summaryAdded", { names: result.added.join(", ") }) });
  if (result.missing.length) lines.push({ key: "missing", text: t("summaryMissing", { names: result.missing.join(", ") }) });
  if (result.restored.length) lines.push({ key: "restored", text: t("summaryRestored", { names: result.restored.join(", ") }) });
  if (result.originUpdated.length)
    lines.push({ key: "origin", text: t("summaryOriginUpdated", { names: result.originUpdated.join(", ") }) });

  return (
    <div className="mb-4 space-y-1.5" data-testid="rescan-summary" aria-live="polite">
      {lines.length === 0 && result.warnings.length === 0 && (
        <p className="text-sm text-emerald-400">{t("upToDate")}</p>
      )}
      {lines.map((line) => (
        <p key={line.key} className="text-sm text-gray-200">
          {line.text}
        </p>
      ))}
      {result.warnings.map((warning) => (
        <p
          key={warning}
          className="rounded-lg border border-amber-500/20 bg-amber-500/10 px-3 py-2 text-sm text-amber-400"
        >
          {warning}
        </p>
      ))}
    </div>
  );
}

function RepoRow({
  projectId,
  repo,
  status,
}: {
  projectId: string;
  repo: ProjectRepo;
  status: RescanRepoStatus | null;
}) {
  const t = useTranslations("dashboard.baseFolder");
  const tFolder = useTranslations("dashboard.fromFolder");
  const format = useFormatter();

  const location = repo.location ?? (isRemoteRepoUrl(repo.gitUrl) ? "remote" : "local");
  const host = location === "remote" ? repoUrlHost(repo.gitUrl) : null;
  const branch = status?.currentBranch ?? null;

  return (
    <li
      className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-3"
      data-testid="base-folder-repo"
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-sm font-medium text-white">{repo.name}</span>
        {repo.folderPath != null && (
          <span className="truncate font-mono text-xs text-gray-400">
            {repo.folderPath || tFolder("rootPath")}
          </span>
        )}
        {location === "remote" ? (
          <Badge variant="info" size="sm" title={repo.gitUrl}>
            {host ? tFolder("remoteAt", { host }) : tFolder("remote")}
          </Badge>
        ) : (
          <Badge variant="default" size="sm" title={repo.gitUrl}>
            {tFolder("localFolder")}
          </Badge>
        )}
        {branch && (
          <span className="font-mono text-xs text-gray-400">{tFolder("onBranch", { branch })}</span>
        )}
        {repo.missingSince && (
          <Badge variant="error" size="sm" title={t("missingHint")}>
            {t("missingSince", {
              date: format.dateTime(new Date(repo.missingSince), { dateStyle: "medium" }),
            })}
          </Badge>
        )}
        {status?.isDirty && (
          <Badge variant="warning" size="sm">
            {tFolder("dirty")}
          </Badge>
        )}
        {status?.hasUnpushedCommits && (
          <Badge variant="warning" size="sm">
            {tFolder("unpushed")}
          </Badge>
        )}
      </div>
      <RepoDescription projectId={projectId} repo={repo} />
    </li>
  );
}

/** Inline edit of the routing description: text, or a textarea while editing → PATCH. */
function RepoDescription({ projectId, repo }: { projectId: string; repo: ProjectRepo }) {
  const t = useTranslations("dashboard.baseFolder");
  const tCommon = useTranslations("common");
  const { getToken } = useAuth();

  const [saved, setSaved] = useState(repo.description ?? "");
  const [draft, setDraft] = useState(saved);
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState(false);

  async function save() {
    if (saving) return;
    setSaving(true);
    setFailed(false);
    try {
      const token = await getToken();
      const updated = await api.repos.update(
        projectId,
        repo.id,
        { defaultBranch: repo.defaultBranch, description: draft.trim() },
        token
      );
      setSaved(updated?.description ?? draft.trim());
      setEditing(false);
      invalidateApiCache();
      refreshData();
    } catch {
      setFailed(true);
    } finally {
      setSaving(false);
    }
  }

  if (editing) {
    return (
      <div className="mt-2 space-y-2">
        <Textarea
          aria-label={t("descriptionLabel")}
          placeholder={t("descriptionPlaceholder")}
          value={draft}
          rows={2}
          maxLength={1000}
          autoFocus
          onChange={(e) => setDraft(e.target.value)}
        />
        {failed && <p className="text-xs text-red-400">{t("descriptionFailed")}</p>}
        <div className="flex justify-end gap-2">
          <Button
            size="sm"
            variant="ghost"
            disabled={saving}
            onClick={() => {
              setDraft(saved);
              setEditing(false);
              setFailed(false);
            }}
          >
            {tCommon("actions.cancel")}
          </Button>
          <Button size="sm" disabled={saving} onClick={save}>
            {saving ? tCommon("actions.saving") : tCommon("actions.save")}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="mt-1.5 flex items-start gap-2">
      <p className={saved ? "text-xs text-gray-300" : "text-xs text-gray-400 italic"}>
        {saved || t("noDescription")}
      </p>
      <RequirePermission permission="ReposManage">
        <button
          type="button"
          onClick={() => {
            setDraft(saved);
            setEditing(true);
          }}
          className="shrink-0 text-xs text-primary-400 transition-colors hover:text-primary-300"
        >
          {saved ? t("editDescription") : t("addDescription")}
        </button>
      </RequirePermission>
    </div>
  );
}
