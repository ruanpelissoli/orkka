import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { api } from "@/lib/api";
import type { RepoSyncStatus } from "@/lib/types";

/**
 * Publishing a linked folder, and keeping it published.
 *
 * The developer creates the empty repository on their forge themselves and pastes its URL —
 * https, ssh or scp-style. Their runner then does the plumbing with their own git
 * credentials: origin, first push, and the ahead/behind pushes after that.
 *
 * The folder stays the working copy throughout. The remote is where it is published, not
 * where the agents work.
 */

function errorText(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  return fallback;
}

interface LocalRepoRemotePanelProps {
  projectId: string;
  repoId: string;
  /** Seeds nothing — shown so the user knows which folder they are publishing. */
  repoName: string;
}

export function LocalRepoRemotePanel({
  projectId,
  repoId,
  repoName,
}: LocalRepoRemotePanelProps) {
  const t = useTranslations("dashboard.localRepos");
  const { getToken } = useAuth();

  const [status, setStatus] = useState<RepoSyncStatus | null>(null);
  const [loading, setLoading] = useState(true);

  const [remoteUrl, setRemoteUrl] = useState("");
  const [connecting, setConnecting] = useState(false);
  const [pushing, setPushing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const loadStatus = useCallback(async (): Promise<RepoSyncStatus | null> => {
    try {
      const token = await getToken();
      return await api.repos.syncStatus(projectId, repoId, token);
    } catch {
      // A status we could not read is not worth an error banner — the actions below
      // report their own failures, which is where the user actually needs the detail.
      return null;
    }
  }, [getToken, projectId, repoId]);

  const refresh = useCallback(async () => {
    setStatus(await loadStatus());
    setLoading(false);
  }, [loadStatus]);

  useEffect(() => {
    loadStatus().then((next) => {
      setStatus(next);
      setLoading(false);
    });
  }, [loadStatus]);

  const handleConnect = useCallback(async () => {
    const url = remoteUrl.trim();
    if (!url || connecting) return;

    setConnecting(true);
    setError(null);
    setNotice(null);
    try {
      const token = await getToken();

      const result = await api.repos.attachRemote(projectId, repoId, url, token);
      setNotice(t("pushedTo", { url: result.remoteUrl }));
      setRemoteUrl("");
      await refresh();
    } catch (err) {
      setError(errorText(err, t("connectFailed")));
    } finally {
      setConnecting(false);
    }
  }, [remoteUrl, connecting, getToken, projectId, repoId, refresh, t]);

  const branch = status?.branch ?? null;
  const handlePush = useCallback(async () => {
    if (pushing) return;
    setPushing(true);
    setError(null);
    setNotice(null);
    try {
      const token = await getToken();

      await api.repos.push(projectId, repoId, branch, token);
      setNotice(t("pushed"));
      await refresh();
    } catch (err) {
      setError(errorText(err, t("pushFailed")));
    } finally {
      setPushing(false);
    }
  }, [pushing, getToken, projectId, repoId, branch, refresh, t]);

  // Nothing to say until we know, and nothing we CAN say when no machine answered.
  if (loading) return null;
  if (!status?.supported) {
    return (
      <p className="text-xs text-gray-400">{t("runnerOfflineHint")}</p>
    );
  }

  return (
    <div className="space-y-3">
      {!status.hasRemote ? (
        <div className="space-y-2">
          <p className="text-xs text-gray-400">
            {t.rich("connectHint", {
              name: repoName,
              mono: (chunks) => (
                <span className="font-mono text-gray-300">{chunks}</span>
              ),
            })}
          </p>
          <p className="text-xs text-amber-400/80">{t("createEmptyHint")}</p>
          <div className="flex gap-2">
            <input
              type="text"
              value={remoteUrl}
              onChange={(e) => setRemoteUrl(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  handleConnect();
                }
              }}
              placeholder="git@github.com:you/your-repo.git"
              disabled={connecting}
              className="flex-1 rounded-lg border border-white/[0.08] bg-white/[0.03] px-3 py-1.5 font-mono text-xs text-gray-200 placeholder:text-gray-500 focus:border-primary-500/50 focus:outline-none disabled:opacity-50"
            />
            <Button
              size="sm"
              variant="secondary"
              disabled={!remoteUrl.trim() || connecting}
              onClick={handleConnect}
            >
              {connecting ? t("connecting") : t("connectAndPush")}
            </Button>
          </div>
        </div>
      ) : (
        <div className="flex items-center justify-between gap-3">
          <p className="text-xs text-gray-400">
            {status.ahead > 0
              ? t("aheadBy", { count: status.ahead, branch: status.branch ?? "" })
              : t("upToDate", { branch: status.branch ?? "" })}
          </p>
          <Button
            size="sm"
            variant="secondary"
            disabled={pushing || status.ahead === 0}
            onClick={handlePush}
          >
            {pushing ? t("pushing") : t("push")}
          </Button>
        </div>
      )}

      {notice && <p className="text-xs text-emerald-400">{notice}</p>}
      {error && <p className="text-xs text-red-400">{error}</p>}
    </div>
  );
}
