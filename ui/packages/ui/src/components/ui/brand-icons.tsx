/**
 * Small line glyphs for where a repository lives and which tracker a connection talks to.
 * Same style as the rest of the app's inline icons (24px box, currentColor stroke), so they
 * sit in lists without looking like pasted-in logos.
 */

interface IconProps {
  size?: number;
  className?: string;
}

/** ProjectRepo.provider values, plus anything unknown (rendered as a plain git remote). */
export type RepoProviderKey = "github" | "azuredevops" | "gitlab" | "bitbucket" | "other" | "local";

const REPO_PATHS: Record<RepoProviderKey, React.ReactNode> = {
  github: (
    <path d="M9 19c-5 1.5-5-2.5-7-3m14 6v-3.87a3.37 3.37 0 00-.94-2.61c3.14-.35 6.44-1.54 6.44-7A5.44 5.44 0 0020 4.77 5.07 5.07 0 0019.91 1S18.73.65 16 2.48a13.38 13.38 0 00-7 0C6.27.65 5.09 1 5.09 1A5.07 5.07 0 005 4.77a5.44 5.44 0 00-1.5 3.78c0 5.42 3.3 6.61 6.44 7A3.37 3.37 0 009 18.13V22" />
  ),
  gitlab: (
    <path d="M12 21.5L2.6 14.7a.9.9 0 01-.3-1l1.8-5.6 2-6.2a.4.4 0 01.8 0l2 6.2h6.2l2-6.2a.4.4 0 01.8 0l2 6.2 1.8 5.6a.9.9 0 01-.3 1z" />
  ),
  bitbucket: (
    <>
      <path d="M3.2 3.5a.7.7 0 00-.7.8l2.6 15.9a1 1 0 001 .8h11.9a.7.7 0 00.7-.6l2.8-16.1a.7.7 0 00-.7-.8z" />
      <path d="M14.3 15H9.8l-1.2-6.5h6.9z" />
    </>
  ),
  azuredevops: (
    <path d="M21 6.5v11L16.5 21l-7-2.5V21L4 13.8l11.3.9V4.5zM4 13.8V8.6L15.3 4.5 9.5 3v2.4L4 8.6" />
  ),
  other: (
    <>
      <circle cx="6" cy="6" r="2" />
      <circle cx="6" cy="18" r="2" />
      <circle cx="18" cy="8" r="2" />
      <path d="M6 8v8M18 10c0 4-6 3-11 6.5" />
    </>
  ),
  local: (
    <path d="M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2z" />
  ),
};

function normalizeRepoProvider(provider: string | null | undefined): RepoProviderKey {
  const key = (provider ?? "").toLowerCase();
  return key in REPO_PATHS ? (key as RepoProviderKey) : "other";
}

/** Where a repo lives: its forge's glyph, a folder for a linked local folder, a branch otherwise. */
export function RepoProviderIcon({
  provider,
  size = 14,
  className,
}: IconProps & { provider: string | null | undefined }) {
  const key = normalizeRepoProvider(provider);
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden
      data-provider-icon={key}
    >
      {REPO_PATHS[key]}
    </svg>
  );
}

/** TrackerConnection.kind — "jira" or "ado". */
export function TrackerKindIcon({ kind, size = 14, className }: IconProps & { kind: string }) {
  const isAdo = kind === "ado";
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden
      data-tracker-icon={isAdo ? "ado" : "jira"}
    >
      {isAdo ? (
        REPO_PATHS.azuredevops
      ) : (
        <>
          <path d="M12 3l9 9-9 9-3-3 6-6-6-6z" />
          <path d="M9 6l-6 6 6 6 3-3-3-3 3-3z" />
        </>
      )}
    </svg>
  );
}
