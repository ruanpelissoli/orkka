import { useId, useState } from "react";
import { useTranslations } from "use-intl";
import { Button, Input, Select } from "@/components/ui";
import { classifyRepoUrl, REPO_FORGES } from "@/lib/repo-url";
import type { AddProjectRepoRequest, RepoForge } from "@/lib/types";
import { cn } from "@/lib/utils";

interface AddRepoByUrlFormProps {
  /** Adds (or queues) the repo. A thrown Error's message is shown under the form. */
  onAdd: (request: AddProjectRepoRequest) => Promise<void> | void;
  /** Overrides the submit button's label (onboarding queues repos rather than adding them). */
  submitLabel?: string;
  /** Optional extra controls next to the submit button. */
  footer?: React.ReactNode;
  className?: string;
}

/**
 * "Add repository by URL": any remote the developer can already clone — https, ssh:// or
 * scp-style, ssh host aliases included. The runner clones and pushes with the developer's own
 * git credentials, so nothing is installed on the forge and no token is ever typed here.
 *
 * The URL is classified as the user types (same rules as the server's RepoUrl), the forge is
 * detected from the host with an override for host aliases, and the name is prefilled.
 */
export function AddRepoByUrlForm({ onAdd, submitLabel, footer, className }: AddRepoByUrlFormProps) {
  const t = useTranslations("dashboard.addRepoByUrl");
  const tForge = useTranslations("dashboard.forges");
  const ids = useId();

  const [url, setUrl] = useState("");
  const [name, setName] = useState("");
  const [nameTouched, setNameTouched] = useState(false);
  const [defaultBranch, setDefaultBranch] = useState("main");
  const [forgeOverride, setForgeOverride] = useState<RepoForge | null>(null);
  const [cliAccount, setCliAccount] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const info = classifyRepoUrl(url);
  const forge: RepoForge = forgeOverride ?? info?.forge ?? "other";
  const effectiveName = nameTouched ? name : (info?.suggestedName ?? "");

  let urlError: string | undefined;
  if (info?.kind === "local") urlError = t("localPath");
  else if (info?.hasEmbeddedCredential) urlError = t("credentialInUrl");
  else if (info && !info.host) urlError = t("noHost");

  const canSubmit =
    !!info?.acceptable && effectiveName.trim().length > 0 && defaultBranch.trim().length > 0;

  function reset() {
    setUrl("");
    setName("");
    setNameTouched(false);
    setDefaultBranch("main");
    setForgeOverride(null);
    setCliAccount("");
  }

  async function submit() {
    if (!canSubmit || submitting) return;
    setSubmitting(true);
    setError(null);
    try {
      await onAdd({
        gitUrl: url.trim(),
        name: effectiveName.trim(),
        defaultBranch: defaultBranch.trim(),
        // Only an override is sent; otherwise the server detects it from the host, as here.
        provider: forgeOverride && forgeOverride !== info?.forge ? forgeOverride : null,
        cliAccount: cliAccount.trim() || null,
      });
      reset();
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : t("addFailed"));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form
      className={cn("space-y-3", className)}
      onSubmit={(e) => {
        e.preventDefault();
        void submit();
      }}
      data-testid="add-repo-by-url"
    >
      <div className="space-y-1.5">
        <Input
          id={`${ids}-url`}
          label={t("urlLabel")}
          placeholder="git@github.com:acme/api.git"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          error={urlError}
          className="font-mono text-xs"
          autoComplete="off"
          spellCheck={false}
          disabled={submitting}
        />
        {info && !urlError && (
          <p className="text-xs text-gray-400" data-testid="repo-url-kind">
            {t(`kind.${info.kind}`)}
            {info.host && <span className="font-mono text-gray-300"> · {info.host}</span>}
          </p>
        )}
        {!info && <p className="text-xs text-gray-400">{t("urlHint")}</p>}
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <Input
          id={`${ids}-name`}
          label={t("nameLabel")}
          value={effectiveName}
          onChange={(e) => {
            setNameTouched(true);
            setName(e.target.value);
          }}
          disabled={submitting}
        />
        <Input
          id={`${ids}-branch`}
          label={t("defaultBranchLabel")}
          value={defaultBranch}
          onChange={(e) => setDefaultBranch(e.target.value)}
          className="font-mono text-xs"
          disabled={submitting}
        />
        <Select
          id={`${ids}-forge`}
          label={t("forgeLabel")}
          value={forge}
          onChange={(e) => setForgeOverride(e.target.value as RepoForge)}
          hint={
            info?.host
              ? info.forge === "other" && !forgeOverride
                ? t("forgeAliasHint", { host: info.host })
                : t("forgeDetectedHint", { host: info.host })
              : undefined
          }
          disabled={submitting}
          data-testid="repo-forge-select"
        >
          {REPO_FORGES.map((f) => (
            <option key={f} value={f}>
              {tForge(f)}
            </option>
          ))}
        </Select>
        <Input
          id={`${ids}-cli-account`}
          label={t("cliAccountLabel")}
          hint={t("cliAccountHint")}
          placeholder={t("cliAccountPlaceholder")}
          value={cliAccount}
          onChange={(e) => setCliAccount(e.target.value)}
          autoComplete="off"
          spellCheck={false}
          disabled={submitting}
        />
      </div>

      {error && <p className="text-xs text-red-400">{error}</p>}

      <div className="flex flex-wrap items-center justify-end gap-2">
        {footer}
        <Button type="submit" size="sm" disabled={!canSubmit || submitting}>
          {submitting ? t("adding") : (submitLabel ?? t("add"))}
        </Button>
      </div>
    </form>
  );
}
