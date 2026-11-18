import { useState } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { api } from "@/lib/api";
import type { RepoAccessCheck } from "@/lib/types";

/**
 * "Can my machine read these repos?" — asked of the caller's own runner before a task finds
 * out the hard way. Repos are cloned there with the developer's own git credentials, so a
 * missing ssh key or credential helper fails every run on that repo until it is fixed.
 */
export function RepoAccessCheckButton({ projectId }: { projectId: string }) {
  const t = useTranslations("dashboard.repoAccessCheck");
  const { getToken } = useAuth();
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<RepoAccessCheck | null>(null);
  const [failed, setFailed] = useState(false);

  async function check() {
    setChecking(true);
    setFailed(false);
    try {
      const token = await getToken();
      setResult(await api.repos.accessCheck(projectId, token));
    } catch {
      setFailed(true);
      setResult(null);
    } finally {
      setChecking(false);
    }
  }

  return (
    <div className="space-y-2" data-testid="repo-access-check">
      <Button type="button" size="sm" variant="secondary" disabled={checking} onClick={check}>
        {checking ? t("checking") : t("check")}
      </Button>

      {failed && <p className="text-xs text-red-400">{t("failed")}</p>}

      {result && !result.supported && (
        <p className="text-xs text-gray-400">{t("unsupported")}</p>
      )}

      {result?.supported && result.error && (
        <p className="text-xs text-red-400">{result.error}</p>
      )}

      {result?.supported && result.repos.length > 0 && (
        <ul className="space-y-1.5">
          {result.repos.map((r) => (
            <li key={r.repoId} className="text-xs">
              <span className={r.ok ? "text-emerald-400" : "text-red-400"}>
                {r.ok ? t("ok", { name: r.name }) : t("notOk", { name: r.name })}
              </span>
              {!r.ok && (r.reason || r.advice) && (
                <span className="block text-gray-400">
                  {[r.reason, r.advice].filter(Boolean).join(" ")}
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
