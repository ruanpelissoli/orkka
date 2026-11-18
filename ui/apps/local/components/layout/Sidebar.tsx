import { useState } from "react";
import { Link } from "@/i18n/navigation";
import { usePathname } from "@/i18n/navigation";
import { useParams } from "@/lib/navigation";
import { cn } from "@/lib/utils";
import { useProject } from "@/hooks/use-project";
import { useTranslations } from "use-intl";
import { NavIcon, type NavItem } from "@/components/ui";
import { getProjectNav, getGlobalNav } from "@/lib/nav";
import { tourAnchor } from "@/components/tour";
import { BrandLockup } from "@/components/ui";

interface SidebarProps {
  className?: string;
  /** Rendered above the collapse toggle. The mobile drawer uses it for the top-bar
   *  controls that do not fit on a phone-width header. */
  footer?: React.ReactNode;
}

function Sidebar({ className, footer }: SidebarProps) {
  const pathname = usePathname();
  const params = useParams();
  const [collapsed, setCollapsed] = useState(false);
  const t = useTranslations("dashboard");

  const projectId = params?.projectId as string | undefined;
  let currentProject = null;
  /** "Back to Project" only means something when there is a project to go back to. */
  let hasProjects = false;
  try {
    const ctx = useProject();
    currentProject = ctx.currentProject;
    hasProjects = ctx.projects.length > 0;
  } catch {
    // Outside ProjectProvider — ignore
  }

  const projectNav = projectId ? getProjectNav(projectId, t) : [];
  const globalNav = getGlobalNav(t);

  function isActive(href: string, exact?: boolean) {
    if (exact) return pathname === href;
    return pathname.startsWith(href);
  }

  function renderNavItem(item: NavItem) {
    const active = isActive(item.href, item.exact);
    return (
      <Link
        key={item.href}
        href={item.href}
        {...tourAnchor(`nav-${item.key}`)}
        className={cn(
          "relative flex items-center gap-3 rounded-xl px-3 py-2.5 text-sm font-medium",
          "transition-all duration-200",
          active
            ? "text-white"
            : "text-gray-400 hover:text-gray-200 hover:bg-white/[0.04]"
        )}
      >
        {active && (
          <div className="absolute inset-0 rounded-xl bg-white/[0.06] border border-white/[0.06]" />
        )}
        <span className="relative shrink-0">
          <NavIcon name={item.icon} />
        </span>
        {!collapsed && <span className="relative">{item.label}</span>}
      </Link>
    );
  }

  return (
    <aside
      className={cn(
        "flex h-screen flex-col border-r border-white/[0.04]",
        "bg-primary-950/80 backdrop-blur-xl",
        "transition-all duration-300 ease-out",
        collapsed ? "w-[68px]" : "w-[240px]",
        className
      )}
    >
      {/* Logo */}
      <div className="flex h-16 items-center border-b border-white/[0.04] px-4">
        <BrandLockup size="md" markOnly={collapsed} />
      </div>

      {/* Nav */}
      <nav className="flex-1 overflow-y-auto p-3">
        {projectId ? (
          <>
            {!collapsed && currentProject && (
              <div className="mb-3 px-3">
                <span className="text-label">
                  {t("sidebar.project")}
                </span>
                <p className="mt-0.5 text-sm font-medium text-gray-200 truncate">
                  {currentProject.name}
                </p>
              </div>
            )}

            <div className="space-y-1">
              {projectNav.map(renderNavItem)}
            </div>

            <div className="my-4 border-t border-white/[0.04]" />

            <div className="space-y-1">
              {globalNav.map(renderNavItem)}
            </div>
          </>
        ) : (
          <>
            {!collapsed && (
              <div className="mb-3 px-3">
                <span className="text-label">
                  {t("sidebar.navigation")}
                </span>
              </div>
            )}
            <div className="space-y-1">
              {hasProjects && (
              <Link
                href="/dashboard"
                className={cn(
                  "relative flex items-center gap-3 rounded-xl px-3 py-2.5 text-sm font-medium",
                  "text-gray-400 hover:text-gray-200 hover:bg-white/[0.04] transition-all duration-200"
                )}
              >
                <span className="relative shrink-0">
                  <NavIcon name="home" />
                </span>
                {!collapsed && <span className="relative">{t("sidebar.backToProject")}</span>}
              </Link>
              )}
              {globalNav.map(renderNavItem)}
            </div>
          </>
        )}
      </nav>

      {footer && (
        <div className="space-y-2 border-t border-white/[0.04] p-3">{footer}</div>
      )}

      {/* Collapse toggle */}
      <div className="border-t border-white/[0.04] p-3">
        <button
          onClick={() => setCollapsed(!collapsed)}
          className="flex w-full items-center justify-center rounded-xl p-2 text-gray-400 transition-colors hover:bg-white/[0.04] hover:text-gray-300"
        >
          <svg
            width="16"
            height="16"
            viewBox="0 0 16 16"
            fill="none"
            className={cn("transition-transform", collapsed && "rotate-180")}
          >
            <path
              d="M10 12L6 8l4-4"
              stroke="currentColor"
              strokeWidth="1.5"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        </button>
      </div>

    </aside>
  );
}

export { Sidebar };
