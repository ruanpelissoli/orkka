import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";

/**
 * Why this task's workspace won't run the app.
 *
 * Deliberately rendered next to the "open in editor" action rather than only in the task's
 * activity feed: the person this matters to is whoever is about to open the worktree and
 * run it, and they would otherwise discover it as a mystery failure of their own commands.
 * The task timeline still carries the full output — this is the headline.
 */
export function SetupFailureNotice({
  reason,
  className,
}: {
  reason: string | null | undefined;
  className?: string;
}) {
  const t = useTranslations("dashboard.taskDetailLive");

  if (!reason) return null;

  return (
    <GlassCard padding="md" accentColor="#f59e0b" className={className}>
      <div className="flex items-start gap-3">
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#f59e0b" strokeWidth="2" className="shrink-0 mt-0.5">
          <path d="M12 9v4M12 17h.01" strokeLinecap="round" />
          <path d="M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" strokeLinejoin="round" />
        </svg>
        <div className="min-w-0">
          <h3 className="font-display text-sm font-semibold text-white">
            {t("setupFailed")}
          </h3>
          <p className="mt-0.5 text-xs text-gray-400">{reason}</p>
          <p className="mt-1 text-xs text-gray-400">{t("setupFailedHint")}</p>
        </div>
      </div>
    </GlassCard>
  );
}
