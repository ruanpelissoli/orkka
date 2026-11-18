import { useTranslations } from "use-intl";
import { detectRepoForge } from "@/lib/repo-url";
import { cn } from "@/lib/utils";

/**
 * "View pull request on <forge>" — the pull request the developer's runner opened on the
 * forge (gh / az / glab) when the reviewed work landed. Orkka's own review record stays in
 * Orkka; this is the forge's copy, where the team takes it from here.
 */
export function ForgePrLink({ url, className }: { url: string; className?: string }) {
  const t = useTranslations("dashboard.forgePr");
  const tForge = useTranslations("dashboard.forges");
  const forge = detectRepoForge(url);
  const label = forge === "other" ? t("view") : t("viewOn", { forge: tForge(forge) });

  return (
    <a
      href={url}
      target="_blank"
      rel="noopener noreferrer"
      data-testid="forge-pr-link"
      className={cn(
        "inline-flex items-center gap-1 rounded-full border border-white/[0.08] bg-white/[0.04] px-2.5 py-0.5 text-xs font-medium text-gray-300 transition-colors hover:bg-white/[0.08] hover:text-white",
        className
      )}
    >
      <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden>
        <path d="M6 3H3v10h10v-3M9 2h5v5M14 2L7 9" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      {label}
    </a>
  );
}
