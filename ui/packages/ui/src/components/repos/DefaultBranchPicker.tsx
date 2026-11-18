import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { api, invalidateApiCache } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { useToast } from "@/components/ui";
import type { RepoBranch } from "@/lib/types";

interface DefaultBranchPickerProps {
  projectId: string;
  repoId: string;
  defaultBranch: string;
  /** Called after a successful change so the parent can refresh its repo list. */
  onChanged?: () => void;
}

/** Shows the repo's default branch (the base for task worktrees, diffs, and merges)
 * and lets the user switch it to any existing branch. */
function DefaultBranchPicker({
  projectId,
  repoId,
  defaultBranch,
  onChanged,
}: DefaultBranchPickerProps) {
  const t = useTranslations("dashboard.defaultBranchPicker");
  const tActions = useTranslations("common.actions");
  const withToken = useClientApi();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [branches, setBranches] = useState<RepoBranch[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  // Branches load lazily on first open — cheap for repos that never touch this.
  const toggleOpen = useCallback(() => {
    const next = !open;
    setOpen(next);
    if (!next || branches !== null || loading) return;
    setLoading(true);
    withToken((token) => api.repoHistory.branches(projectId, repoId, token))
      .then(setBranches)
      .catch(() => setBranches([]))
      .finally(() => setLoading(false));
  }, [open, branches, loading, withToken, projectId, repoId]);

  // Close on outside click.
  useEffect(() => {
    if (!open) return;
    function onPointerDown(e: PointerEvent) {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [open]);

  const selectBranch = useCallback(
    async (name: string) => {
      setOpen(false);
      if (name === defaultBranch || saving) return;
      setSaving(true);
      try {
        await withToken((token) =>
          api.repos.update(projectId, repoId, { defaultBranch: name }, token)
        );
        invalidateApiCache();
        toast(t("changed", { name }));
        onChanged?.();
      } catch {
        toast(t("changeFailed"), "error");
      } finally {
        setSaving(false);
      }
    },
    [defaultBranch, saving, withToken, projectId, repoId, toast, onChanged, t]
  );

  return (
    <div ref={rootRef} className="relative">
      <button
        type="button"
        onClick={toggleOpen}
        disabled={saving}
        title={t("title")}
        className={cn(
          "flex items-center gap-1.5 rounded-lg border border-white/10 bg-white/5 px-2.5 py-1.5",
          "text-xs font-mono text-gray-300 transition-colors",
          "hover:border-white/20 hover:bg-white/10",
          saving && "opacity-60"
        )}
      >
        <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" className="shrink-0 text-gray-400">
          <circle cx="5" cy="3.5" r="1.5" />
          <circle cx="5" cy="12.5" r="1.5" />
          <circle cx="11" cy="6.5" r="1.5" />
          <path d="M5 5v6M11 8a4 4 0 01-4 4" strokeLinecap="round" />
        </svg>
        <span className="max-w-[140px] truncate">{saving ? tActions("saving") : defaultBranch}</span>
        <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0 text-gray-400">
          <path d="M19 9l-7 7-7-7" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open && (
        <div className="absolute right-0 top-full z-50 mt-1 max-h-64 w-64 overflow-y-auto rounded-xl border border-white/10 bg-gray-900/95 p-1 shadow-xl backdrop-blur-md">
          <p className="px-2.5 py-1.5 text-label">
            {t("heading")}
          </p>
          {loading && (
            <p className="px-2.5 py-2 text-xs text-gray-400">{t("loading")}</p>
          )}
          {!loading && branches !== null && branches.length === 0 && (
            <p className="px-2.5 py-2 text-xs text-gray-400">{t("empty")}</p>
          )}
          {!loading &&
            branches?.map((b) => (
              <button
                key={b.name}
                type="button"
                onClick={() => selectBranch(b.name)}
                className={cn(
                  "flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-xs font-mono",
                  "transition-colors hover:bg-white/[0.06]",
                  b.name === defaultBranch ? "text-primary-400" : "text-gray-300"
                )}
              >
                <span className="flex-1 truncate">{b.name}</span>
                {b.isRemote && (
                  <span
                    title={t("remoteTitle")}
                    className="shrink-0 rounded-full border border-amber-500/20 bg-amber-500/10 px-1.5 py-px text-label text-amber-400"
                  >
                    {t("remoteBadge")}
                  </span>
                )}
                {b.name === defaultBranch && (
                  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0">
                    <path d="M20 6L9 17l-5-5" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                )}
              </button>
            ))}
        </div>
      )}
    </div>
  );
}

export { DefaultBranchPicker };
