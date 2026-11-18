import { Link } from "@/i18n/navigation";
import { usePathname } from "@/i18n/navigation";
import { useParams } from "@/lib/navigation";
import { ProjectSwitcher } from "@/components/project/ProjectSwitcher";
import { WorkspaceSwitcher } from "@/components/workspace/WorkspaceSwitcher";
import { LocaleSwitcher } from "@/components/ui/LocaleSwitcher";
import { NotificationBell } from "@/components/notifications/NotificationBell";
import { LocalMenu } from "./LocalMenu";
import { useProject } from "@/hooks/use-project";
import { useTranslations } from "use-intl";
import { buildCrumbs } from "@/lib/breadcrumbs";

interface TopBarProps {
  onMenuToggle?: () => void;
}

function TopBar({ onMenuToggle }: TopBarProps) {
  const pathname = usePathname();
  const params = useParams();
  const projectId = params?.projectId as string | undefined;
  const t = useTranslations("dashboard");
  const tCommon = useTranslations("common");

  let currentProject = null;
  try {
    const ctx = useProject();
    currentProject = ctx.currentProject;
  } catch {
    // Outside ProjectProvider
  }

  // Breadcrumbs: each section in the reader's language (the labels the sidebar and settings
  // nav already use), the project id as the project's name, other ids as typed.
  const segments = buildCrumbs(pathname, projectId).map((crumb) => ({
    href: crumb.href,
    isCurrent: crumb.isCurrent,
    label: crumb.isProject
      ? (currentProject?.name ?? t("topBar.project"))
      : crumb.labelKey
        ? (crumb.labelKey.namespace === "common" ? tCommon : t)(crumb.labelKey.key)
        : crumb.text,
  }));

  return (
    <header className="relative z-20 flex h-14 items-center gap-2 border-b border-white/[0.04] bg-gray-950/60 px-3 backdrop-blur-xl lg:gap-4 lg:px-6">
      {/* Mobile menu button */}
      <button
        onClick={onMenuToggle}
        className="shrink-0 rounded-lg p-2 text-gray-400 transition-colors hover:bg-white/[0.04] hover:text-gray-200 lg:hidden"
        aria-label={t("topBar.toggleSidebar")}
      >
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M3 12h18M3 6h18M3 18h18" strokeLinecap="round" />
        </svg>
      </button>

      {/* Workspace (job) switcher, then the project within it */}
      <WorkspaceSwitcher />
      <span className="hidden font-mono text-gray-400 sm:inline" aria-hidden>/</span>
      <ProjectSwitcher />

      {/* Breadcrumbs — hidden on phones. They cannot shrink below their content, so on a
          narrow screen they push the whole right-hand cluster past the edge; the project
          switcher already names where you are. */}
      <nav className="hidden min-w-0 items-center gap-1 overflow-x-auto text-sm md:flex">
        {segments.map((segment, i) => (
          <span key={segment.href} className="flex items-center gap-1 shrink-0">
            {i > 0 && <span className="font-mono text-gray-400">/</span>}
            {segment.isCurrent ? (
              <span className="font-medium text-gray-200">{segment.label}</span>
            ) : (
              <Link
                href={segment.href}
                className="text-gray-400 transition-colors hover:text-gray-200"
              >
                {segment.label}
              </Link>
            )}
          </span>
        ))}
      </nav>

      <div className="flex-1" />

      {/* Right side items. shrink-0 is load-bearing: DashboardShell clips this header's
          overflow, so anything pushed past the right edge is gone, not scrolled to — which
          is how the menu went missing on mobile. */}
      <div className="flex shrink-0 items-center gap-1 lg:gap-3">
        {/* Below md these move into the mobile drawer — see DashboardShell. */}
        <div className="hidden items-center gap-3 md:flex">
          <LocaleSwitcher />
        </div>

        <NotificationBell />

        <LocalMenu />
      </div>
    </header>
  );
}

export { TopBar };
