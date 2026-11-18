import { useState } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useTranslations } from "use-intl";
import { api, extractErrorMessage } from "@/lib/api";
import { Button, Modal, Textarea } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import type { ProjectRepo } from "@/lib/types";

/**
 * The four commands that make a task worktree usable: one to prepare it (`npm ci`), one to
 * start the app in it (`npm run dev`), one to prove the code still compiles
 * (`npm run build`), and one to prove a change is good — lint, typecheck, tests
 * (`npm run lint && npm test`) — run after every dev run.
 *
 * All per repo, not per project, because worktrees are per repo — a Next.js frontend and a
 * .NET backend in the same project need different commands, and a single project-level field
 * can only ever describe one of them.
 */
export function RepoSetupCommandButton({
  projectId,
  repo,
  onSaved,
}: {
  projectId: string;
  repo: ProjectRepo;
  onSaved?: (repo: ProjectRepo) => void;
}) {
  const t = useTranslations("dashboard.reposPage");
  const { getToken } = useAuth();
  const [open, setOpen] = useState(false);
  const [setupCommand, setSetupCommand] = useState(repo.setupCommand ?? "");
  const [qaRunCommand, setQaRunCommand] = useState(repo.qaRunCommand ?? "");
  const [buildCommand, setBuildCommand] = useState(repo.buildCommand ?? "");
  const [checkCommand, setCheckCommand] = useState(repo.checkCommand ?? "");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function handleOpen() {
    setSetupCommand(repo.setupCommand ?? "");
    setQaRunCommand(repo.qaRunCommand ?? "");
    setBuildCommand(repo.buildCommand ?? "");
    setCheckCommand(repo.checkCommand ?? "");
    setError(null);
    setOpen(true);
  }

  async function handleSave() {
    setSaving(true);
    setError(null);
    try {
      const token = await getToken();
      const updated = await api.repos.update(
        projectId,
        repo.id,
        {
          defaultBranch: repo.defaultBranch,
          setupCommand: setupCommand.trim(),
          qaRunCommand: qaRunCommand.trim(),
          buildCommand: buildCommand.trim(),
          checkCommand: checkCommand.trim(),
        },
        token
      );
      onSaved?.(updated);
      setOpen(false);
    } catch (e) {
      setError(extractErrorMessage(e, t("setupSaveFailed")));
    } finally {
      setSaving(false);
    }
  }

  return (
    <RequirePermission permission="ReposManage">
      <Button
        variant="ghost"
        size="sm"
        onClick={handleOpen}
        aria-label={t("workspaceSettings")}
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="text-gray-400 hover:text-gray-200">
          <path d="M4 17l6-6-6-6M12 19h8" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </Button>

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={t("workspaceTitle", { name: repo.name })}
      >
        <div className="space-y-5">
          <div className="space-y-2">
            <p className="text-sm font-medium text-gray-300">{t("setupCommand")}</p>
            <p className="text-xs text-gray-400">{t("setupDescription")}</p>
            <Textarea
              value={setupCommand}
              onChange={(e) => setSetupCommand(e.target.value)}
              placeholder="npm ci"
              rows={2}
              className="font-mono text-xs"
            />
          </div>

          <div className="space-y-2">
            <p className="text-sm font-medium text-gray-300">{t("runCommand")}</p>
            <p className="text-xs text-gray-400">{t("runDescription")}</p>
            <Textarea
              value={qaRunCommand}
              onChange={(e) => setQaRunCommand(e.target.value)}
              placeholder="npm run dev"
              rows={2}
              className="font-mono text-xs"
            />
          </div>

          <div className="space-y-2">
            <p className="text-sm font-medium text-gray-300">{t("buildCommand")}</p>
            <p className="text-xs text-gray-400">{t("buildDescription")}</p>
            <Textarea
              value={buildCommand}
              onChange={(e) => setBuildCommand(e.target.value)}
              placeholder="npm run build"
              rows={2}
              className="font-mono text-xs"
            />
          </div>

          <div className="space-y-2">
            <p className="text-sm font-medium text-gray-300">{t("checkCommand")}</p>
            <p className="text-xs text-gray-400">{t("checkCommandHelp")}</p>
            <Textarea
              value={checkCommand}
              onChange={(e) => setCheckCommand(e.target.value)}
              placeholder="npm run lint && npm test"
              rows={2}
              className="font-mono text-xs"
            />
          </div>

          {/* Not a hint we can drop: a secret typed here would be stored by Orkka and would
              run on our infrastructure for cloud tasks. Copying their own file on their own
              machine is the supported path. */}
          <p className="rounded-xl border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs text-gray-400">
            {t("setupSecretsHint")}
          </p>

          {error && <p className="text-xs text-red-400">{error}</p>}

          <div className="flex justify-end gap-2">
            <Button variant="ghost" size="sm" onClick={() => setOpen(false)}>
              {t("setupCancel")}
            </Button>
            <Button size="sm" loading={saving} onClick={() => void handleSave()}>
              {t("setupSave")}
            </Button>
          </div>
        </div>
      </Modal>
    </RequirePermission>
  );
}
