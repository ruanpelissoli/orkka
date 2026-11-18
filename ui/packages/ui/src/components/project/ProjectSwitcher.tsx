import { useState, useRef, useEffect } from "react";
import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { useProject } from "@/hooks/use-project";

interface ProjectSwitcherProps {
  /** Where "New Project" navigates. */
  newProjectHref?: string;
}

export function ProjectSwitcher({ newProjectHref = "/projects/new" }: ProjectSwitcherProps = {}) {
  const t = useTranslations("dashboard.uiMisc");
  const { projects, currentProject, isLoading } = useProject();
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Close dropdown on outside click
  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    if (open) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => document.removeEventListener("mousedown", handleClickOutside);
    }
  }, [open]);

  if (isLoading) {
    return (
      <div className="h-8 w-32 animate-pulse rounded-lg bg-white/[0.04]" />
    );
  }

  if (!currentProject && projects.length === 0) {
    return null;
  }

  return (
    <div ref={ref} className="relative min-w-0">
      <button
        onClick={() => setOpen(!open)}
        className={cn(
          "flex items-center gap-2 rounded-lg px-3 py-1.5 text-sm font-medium",
          "transition-colors hover:bg-white/[0.06]",
          open ? "bg-white/[0.06] text-white" : "text-gray-200"
        )}
      >
        {/* Project color dot */}
        <span
          className="h-2 w-2 shrink-0 rounded-full bg-primary-500"
          style={{ boxShadow: "0 0 6px rgba(59,130,246,0.4)" }}
        />
        <span className="max-w-[110px] truncate sm:max-w-[160px]">
          {currentProject?.name ?? t("selectProject")}
        </span>
        <svg
          width="12"
          height="12"
          viewBox="0 0 12 12"
          fill="none"
          className={cn(
            "shrink-0 text-gray-400 transition-transform",
            open && "rotate-180"
          )}
        >
          <path d="M3 4.5l3 3 3-3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open && (
        <div className="absolute left-0 top-full z-50 mt-1 w-64 rounded-xl border border-white/[0.06] bg-gray-900 p-1.5 shadow-2xl">
          {/* Project list */}
          <div className="max-h-60 overflow-y-auto">
            {projects.map((project) => (
              <button
                key={project.id}
                onClick={() => {
                  router.push(`/p/${project.slug}`);
                  setOpen(false);
                }}
                className={cn(
                  "flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left text-sm transition-colors",
                  project.id === currentProject?.id
                    ? "bg-white/[0.06] text-white"
                    : "text-gray-300 hover:bg-white/[0.04] hover:text-white"
                )}
              >
                <span
                  className={cn(
                    "h-2 w-2 shrink-0 rounded-full",
                    project.id === currentProject?.id
                      ? "bg-primary-500"
                      : "bg-gray-600"
                  )}
                />
                <span className="truncate">{project.name}</span>
                {project.id === currentProject?.id && (
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="ml-auto shrink-0 text-primary-400">
                    <path d="M20 6L9 17l-5-5" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                )}
              </button>
            ))}
          </div>

          {/* Separator + create */}
          <RequirePermission permission="ProjectsCreate">
            <div className="mt-1 border-t border-white/[0.06] pt-1">
              <button
                onClick={() => {
                  router.push(newProjectHref);
                  setOpen(false);
                }}
                className="flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left text-sm text-gray-400 transition-colors hover:bg-white/[0.04] hover:text-white"
              >
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
                </svg>
                <span>{t("newProject")}</span>
              </button>
            </div>
          </RequirePermission>
        </div>
      )}
    </div>
  );
}
