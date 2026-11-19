import { useState } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, GlassCard, Input, Select, Textarea } from "@/components/ui";
import { BaseFolderPanel } from "@/app/[locale]/(dashboard)/p/[projectId]/repos/BaseFolderPanel";
import { Link } from "@/i18n/navigation";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage, invalidateApiCache } from "@/lib/api";
import { folderChecks, forgeCliFor } from "@/lib/project-setup";
import type {
  Project,
  ProjectRepo,
  ProjectRunConfig,
  RepoCommandsCheck,
  RepoForge,
} from "@/lib/types";
import { SetupCheckList } from "./SetupCheckList";

const FORGES: RepoForge[] = ["github", "azuredevops", "gitlab", "bitbucket", "other"];

type CommandField = "setupCommand" | "checkCommand" | "buildCommand" | "qaRunCommand";
const COMMAND_FIELDS: { field: CommandField; kind: string; placeholder: string }[] = [
  { field: "setupCommand", kind: "setup", placeholder: "npm ci" },
  { field: "checkCommand", kind: "check", placeholder: "npm run lint && npm test" },
  { field: "buildCommand", kind: "build", placeholder: "npm run build" },
  { field: "qaRunCommand", kind: "qaRun", placeholder: "npm run dev" },
];

/**
 * Step 1 — the base folder and what is in it. The folder panel of the Repos page (rescan,
 * routing descriptions), then one card per repository: its default branch, how its work lands
 * (a forge pull request through the developer's CLI, or a squash into the linked folder), the
 * forge and CLI account, and the commands that make a task worktree usable — each checked
 * against this machine's CommandTrust, with the exact `./orkka commands approve` to run when it
 * would be refused.
 */
export function FolderStep({
  projectId,
  project,
  repos,
  commands,
  runConfig,
  onChanged,
}: {
  projectId: string;
  project: Project;
  repos: ProjectRepo[];
  commands: RepoCommandsCheck | null;
  runConfig: ProjectRunConfig | null;
  onChanged: () => void;
}) {
  const t = useTranslations("dashboard.projectSetup.folder");
  const checks = folderChecks({ repos, commands });

  return (
    <div className="space-y-4" data-testid="setup-folder-step">
      <BaseFolderPanel
        projectId={projectId}
        project={{ baseFolder: project.baseFolder ?? null, baseFolderShape: project.baseFolderShape ?? null }}
        repos={repos}
      />

      {repos.length === 0 && (
        <GlassCard padding="md">
          <p className="text-sm text-gray-300">{t("noRepos")}</p>
          <Link href={`/p/${project.slug}/repos`} className="mt-2 inline-block text-sm text-cyan-400 hover:text-cyan-300">
            {t("addRepos")}
          </Link>
        </GlassCard>
      )}

      {repos.map((repo) => (
        <SetupRepoCard
          key={repo.id}
          projectId={projectId}
          repo={repo}
          commands={commands?.repos.find((r) => r.repoId === repo.id)?.commands ?? null}
          onSaved={onChanged}
        />
      ))}

      {commands && !commands.supported && repos.length > 0 && (
        <p className="text-xs text-amber-300" data-testid="setup-commands-offline">
          {t("commandsOffline")}
        </p>
      )}

      <GlassCard padding="md" data-testid="setup-run-contract">
        <h3 className="text-sm font-medium text-gray-100">{t("runContractTitle")}</h3>
        <p className="mt-1 text-xs text-gray-400">
          {runConfig?.source === "manifest" && runConfig.targets.length > 0
            ? t("runContractManifest", { count: runConfig.targets.length })
            : runConfig?.detected && runConfig.targets.length > 0
              ? t("runContractDetected", { count: runConfig.targets.length })
              : t("runContractNone")}
        </p>
      </GlassCard>

      <SetupCheckList checks={checks.filter((c) => c.level !== "ok")} testId="setup-folder-checks" />
    </div>
  );
}

function SetupRepoCard({
  projectId,
  repo,
  commands,
  onSaved,
}: {
  projectId: string;
  repo: ProjectRepo;
  commands: RepoCommandsCheck["repos"][number]["commands"] | null;
  onSaved: () => void;
}) {
  const t = useTranslations("dashboard.projectSetup.folder");
  const tForge = useTranslations("dashboard.forges");
  const withToken = useClientApi();
  const local = repo.location === "local" || repo.provider === "local";
  const cli = forgeCliFor(repo);

  const [defaultBranch, setDefaultBranch] = useState(repo.defaultBranch);
  const [forge, setForge] = useState<RepoForge>(
    (FORGES as string[]).includes(repo.provider) ? (repo.provider as RepoForge) : "other"
  );
  const [cliAccount, setCliAccount] = useState(repo.cliAccount ?? "");
  const [values, setValues] = useState<Record<CommandField, string>>({
    setupCommand: repo.setupCommand ?? "",
    checkCommand: repo.checkCommand ?? "",
    buildCommand: repo.buildCommand ?? "",
    qaRunCommand: repo.qaRunCommand ?? "",
  });
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null);

  async function save() {
    if (saving || !defaultBranch.trim()) return;
    setSaving(true);
    setNotice(null);
    try {
      await withToken((token) =>
        api.repos.update(
          projectId,
          repo.id,
          {
            defaultBranch: defaultBranch.trim(),
            setupCommand: values.setupCommand.trim(),
            checkCommand: values.checkCommand.trim(),
            buildCommand: values.buildCommand.trim(),
            qaRunCommand: values.qaRunCommand.trim(),
            ...(local ? {} : { provider: forge, cliAccount: cliAccount.trim() }),
          },
          token
        )
      );
      invalidateApiCache();
      setNotice({ ok: true, text: t("saved") });
      onSaved();
    } catch (e) {
      setNotice({ ok: false, text: extractErrorMessage(e, t("saveFailed")) });
    } finally {
      setSaving(false);
    }
  }

  return (
    <GlassCard padding="md" data-testid="setup-repo-card" data-repo={repo.name}>
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-medium text-white">{repo.name}</h3>
        <Badge variant={local ? "default" : "info"} size="sm" data-testid="setup-repo-mode">
          {local ? t("modeLocal") : t("modeRemote")}
        </Badge>
        {repo.folderPath != null && (
          <span className="truncate font-mono text-xs text-gray-400">{repo.folderPath || "."}</span>
        )}
      </div>
      <p className="mt-1 text-xs text-gray-400">{local ? t("modeLocalHint") : t("modeRemoteHint")}</p>

      <div className="mt-4 grid gap-3 sm:grid-cols-3">
        <Input
          label={t("defaultBranch")}
          value={defaultBranch}
          onChange={(e) => setDefaultBranch(e.target.value)}
          className="font-mono text-xs"
        />
        {!local && (
          <>
            <Select
              label={t("forge")}
              value={forge}
              onChange={(e) => setForge(e.target.value as RepoForge)}
              data-testid="setup-repo-forge"
            >
              {FORGES.map((f) => (
                <option key={f} value={f}>
                  {tForge(f)}
                </option>
              ))}
            </Select>
            <Input
              label={t("cliAccount")}
              hint={cli ? t("cliAccountHint", { cli }) : t("noCliHint")}
              value={cliAccount}
              onChange={(e) => setCliAccount(e.target.value)}
              placeholder={t("cliAccountPlaceholder")}
            />
          </>
        )}
      </div>

      <div className="mt-4 space-y-3">
        <p className="text-xs text-gray-400">{t("commandsHint")}</p>
        {COMMAND_FIELDS.map(({ field, kind, placeholder }) => {
          const vetted = commands?.find((c) => c.kind === kind);
          return (
            <div key={field} className="space-y-1">
              <Textarea
                label={t(`command.${kind}`)}
                value={values[field]}
                onChange={(e) => setValues((prev) => ({ ...prev, [field]: e.target.value }))}
                placeholder={placeholder}
                rows={1}
                className="font-mono text-xs"
              />
              {vetted && (
                <p
                  className={vetted.allowed ? "text-xs text-emerald-400" : "text-xs text-amber-300"}
                  data-testid={`setup-command-${kind}`}
                >
                  {vetted.allowed
                    ? vetted.source === "approved"
                      ? t("commandApproved")
                      : t("commandDeclared")
                    : vetted.approvalId
                      ? t("commandNeedsApproval", { command: `./orkka commands approve ${vetted.approvalId}` })
                      : (vetted.error ?? t("commandUnchecked"))}
                </p>
              )}
            </div>
          );
        })}
        <p className="text-xs text-gray-400">{t("secretsHint")}</p>
      </div>

      <div className="mt-4 flex flex-wrap items-center gap-3">
        <Button size="sm" onClick={() => void save()} disabled={saving || !defaultBranch.trim()}>
          {saving ? t("saving") : t("save")}
        </Button>
        {notice && (
          <span className={notice.ok ? "text-xs text-emerald-400" : "text-xs text-red-400"}>{notice.text}</span>
        )}
      </div>
    </GlassCard>
  );
}
