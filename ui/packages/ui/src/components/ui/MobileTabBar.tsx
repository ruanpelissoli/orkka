import { Link, usePathname } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { NavIcon, type MobileTab } from "./nav-icons";

/**
 * Fixed bottom tab bar for mobile (hidden at lg+). Tabs arrive with labels
 * already translated; isCreate renders the center FAB, optionally gated by
 * a permission via RequirePermission. Pages under the bar need pb-20 lg:pb-0.
 */
function MobileTabBar({ tabs }: { tabs: MobileTab[] }) {
  const pathname = usePathname();

  return (
    <nav className="fixed inset-x-0 bottom-0 z-50 border-t border-white/[0.04] bg-gray-950/90 backdrop-blur-2xl pb-[env(safe-area-inset-bottom)] lg:hidden">
      <div className="flex items-center justify-around px-2 py-1">
        {tabs.map((tab) => {
          const isActive = tab.exact ? pathname === tab.href : pathname.startsWith(tab.href);

          if (tab.isCreate) {
            const fab = (
              <Link href={tab.href} className="relative -mt-4 flex flex-col items-center">
                <div className="flex h-12 w-12 items-center justify-center rounded-full bg-gradient-to-br from-primary-500 to-primary-700 text-white shadow-[0_0_20px_rgba(59,130,246,0.3)] transition-transform active:scale-95">
                  <NavIcon name={tab.icon} size={22} strokeWidth={2} />
                </div>
                <span className="mt-0.5 text-xs text-gray-400">{tab.label}</span>
              </Link>
            );
            return tab.permission ? (
              <RequirePermission key={tab.key} permission={tab.permission}>
                {fab}
              </RequirePermission>
            ) : (
              <span key={tab.key} className="contents">
                {fab}
              </span>
            );
          }

          return (
            <Link
              key={tab.key}
              href={tab.href}
              className={cn(
                "flex flex-col items-center gap-0.5 rounded-lg px-3 py-2 transition-all",
                isActive ? "text-primary-400" : "text-gray-400"
              )}
            >
              <span
                className={cn(
                  "transition-all",
                  isActive && "scale-110 drop-shadow-[0_0_8px_rgba(59,130,246,0.4)]"
                )}
              >
                <NavIcon name={tab.icon} size={20} />
              </span>
              <span className="text-xs font-medium">{tab.label}</span>
            </Link>
          );
        })}
      </div>
    </nav>
  );
}

export { MobileTabBar };
