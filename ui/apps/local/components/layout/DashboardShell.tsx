import { useState, useCallback, useEffect, useRef } from "react";
import { useParams } from "@/lib/navigation";
import { usePathname } from "@/i18n/navigation";
import { useTranslations } from "use-intl";
import { Sidebar } from "./Sidebar";
import { TopBar } from "./TopBar";
import { FloatingDockHost, MobileDrawer, MobileTabBar, LocaleSwitcher } from "@/components/ui";
import { RunDock } from "@/components/run/RunDock";
import { getMobileTabs } from "@/lib/nav";
import { ProductTour } from "@/components/tour";

function DashboardShell({ children }: { children: React.ReactNode }) {
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const params = useParams();
  const projectId = params?.projectId as string | undefined;
  const t = useTranslations("dashboard");
  const pathname = usePathname();
  const mainRef = useRef<HTMLElement>(null);

  // The page scrolls inside <main>, not the window, so the router's scroll reset never reaches
  // it: a new page starts at the top here.
  useEffect(() => {
    mainRef.current?.scrollTo({ top: 0 });
  }, [pathname]);

  const handleMenuToggle = useCallback(() => {
    setSidebarOpen((prev) => !prev);
  }, []);

  const handleClose = useCallback(() => {
    setSidebarOpen(false);
  }, []);

  return (
    <div className="flex h-screen overflow-hidden">
      {/* Sidebar - desktop only */}
      <div className="hidden lg:block">
        <Sidebar />
      </div>

      {/* Mobile sidebar overlay. The footer carries the top-bar controls that the header
          drops below md — hiding them there would otherwise put them out of reach. */}
      <MobileDrawer open={sidebarOpen} onClose={handleClose}>
        <Sidebar
          className="w-full"
          footer={<LocaleSwitcher fullWidth />}
        />
      </MobileDrawer>

      {/* Main content area */}
      <div className="flex flex-1 flex-col overflow-hidden">
        <TopBar onMenuToggle={handleMenuToggle} />
        <main ref={mainRef} className="flex-1 overflow-y-auto pb-20 lg:pb-0">
          <div className="noise min-h-full flex flex-col">
            {children}
          </div>
        </main>
      </div>

      {/* Mobile nav - mobile only */}
      <MobileTabBar tabs={getMobileTabs(projectId, t)} />

      {/* The one column the floating launchers below stack into, so no two of them
          (run, a page's agent chat) can land on the same corner */}
      <FloatingDockHost />

      {/* Run the project on the user's own machine (via their connected runner). Mounted
          once here so the apps it starts — which outlive any tab — keep one owner. Renders
          nothing for users without an online, app-run-capable runner. */}
      <RunDock />

      {/* One-time guided tour of every page, shown once a member's first project is Ready.
          Mounted here rather than per page so the step index survives the navigations the tour
          itself drives — a per-page mount would restart it on every Next. */}
      <ProductTour />
    </div>
  );
}

export { DashboardShell };
