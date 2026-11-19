import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import type { Project } from "@/lib/types";

/**
 * On a project whose guided setup was left before Start: its ticket source is not polled yet.
 * One line, linking to the setup page, which resumes at the first step not confirmed.
 */
export function FinishSetupBanner({
  project,
  className,
}: {
  project: Pick<Project, "slug" | "setupCompletedAt">;
  className?: string;
}) {
  const t = useTranslations("dashboard.projectSetup.banner");
  if (project.setupCompletedAt !== null) return null; // started, or an API older than the setup flow

  return (
    <div
      className={`flex flex-wrap items-center justify-between gap-3 rounded-lg border border-amber-500/20 bg-amber-500/[0.06] px-4 py-2.5 ${className ?? ""}`}
      data-testid="finish-setup-banner"
      role="status"
    >
      <p className="text-sm text-amber-300">{t("text")}</p>
      <Link
        href={`/p/${project.slug}/setup`}
        className="text-sm font-medium text-amber-300 underline decoration-current/40 underline-offset-2 hover:text-white"
      >
        {t("action")}
      </Link>
    </div>
  );
}
