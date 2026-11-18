import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { Button } from "@/components/ui";
import { startInteractiveSession } from "@/lib/actions";
import type { ProjectRepo } from "@/lib/types";

/**
 * Opens an interactive session on one of the project's repos.
 *
 * Deliberately not a form. A session exists for work that was not worth filling in a form for —
 * priority, type and even the title are defaulted, and the title gets named later from the
 * conversation. The only thing that genuinely cannot be guessed is which repo, and when there is
 * only one, this is a single click.
 */
export function StartSessionButton({
  projectId,
  repos,
}: {
  projectId: string;
  repos: ProjectRepo[];
}) {
  const t = useTranslations("dashboard.interactiveSession");
  const router = useRouter();
  const [pending, startTransition] = useTransition();
  const [pickerOpen, setPickerOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // A session edits code on a branch, so with no repo there is nothing it could do.
  if (repos.length === 0) return null;

  function start(repoId: string) {
    setError(null);
    setPickerOpen(false);
    startTransition(async () => {
      const result = await startInteractiveSession(projectId, repoId);
      if (result.error !== null) setError(result.error);
      else router.push(result.path);
    });
  }

  function handleClick() {
    if (repos.length === 1) start(repos[0].id);
    else setPickerOpen((open) => !open);
  }

  return (
    <div className="relative">
      <Button size="sm" variant="secondary" onClick={handleClick} disabled={pending}>
        <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
          <path d="M2 3.5h12v8H9l-3 2.5v-2.5H2z" strokeLinejoin="round" />
        </svg>
        {pending ? t("starting") : t("start")}
      </Button>

      {pickerOpen && (
        <div
          className="absolute right-0 z-20 mt-2 min-w-[12rem] rounded-xl border border-white/[0.08] bg-gray-900/95 p-1 backdrop-blur-sm shadow-lg"
          role="menu"
        >
          <p className="px-3 py-1.5 text-label">
            {t("chooseRepo")}
          </p>
          {repos.map((repo) => (
            <button
              key={repo.id}
              type="button"
              role="menuitem"
              onClick={() => start(repo.id)}
              className="block w-full truncate rounded-lg px-3 py-2 text-left text-sm text-gray-200 hover:bg-white/[0.06]"
            >
              {repo.name}
            </button>
          ))}
        </div>
      )}

      {error && (
        <p className="absolute right-0 mt-2 text-xs text-red-400" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
