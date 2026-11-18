import { useTranslations } from "use-intl";
import { Skeleton, SkeletonCard } from "@/components/ui";
import { OrcaLoading } from "@/components/animations";

/** The generic page skeleton — what (dashboard)/loading.tsx showed while a page loaded. */
export function DashboardSkeleton() {
  const t = useTranslations("common");
  return (
    <div className="mx-auto max-w-7xl space-y-8 p-4 lg:p-8" data-testid="page-loading">
      <div className="flex justify-center py-8">
        <OrcaLoading size="md" title={t("actions.loading")} />
      </div>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <SkeletonCard key={i} />
        ))}
      </div>
      <div className="grid gap-6 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <SkeletonCard />
        </div>
        <SkeletonCard />
      </div>
    </div>
  );
}

/** Full-screen loading for the dashboard shell itself (workspaces + permissions). */
export function ShellLoading() {
  const t = useTranslations("common");
  return (
    <div className="flex min-h-screen items-center justify-center" data-testid="page-loading">
      <OrcaLoading size="md" title={t("actions.loading")} />
    </div>
  );
}

function SkeletonLane() {
  return (
    <div className="flex-1 min-w-[280px] md:min-w-0">
      <div className="mb-3 flex items-center gap-2 px-1">
        <Skeleton variant="circular" className="h-2.5 w-2.5" />
        <Skeleton className="h-3 w-20" />
        <Skeleton className="ml-auto h-3 w-4" />
      </div>
      <div className="space-y-2 rounded-xl p-2">
        {Array.from({ length: 3 }).map((_, i) => (
          <div key={i} className="rounded-xl border border-white/[0.06] bg-white/[0.03] p-3 space-y-2">
            <Skeleton className="h-4 w-4/5" />
            <div className="flex gap-2">
              <Skeleton variant="circular" className="h-1.5 w-1.5" />
              <Skeleton className="h-3 w-16" />
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

export function BoardSkeleton() {
  return (
    <div className="py-4" data-testid="page-loading">
      <div className="mb-4 flex items-center justify-between px-4 lg:px-8">
        <Skeleton className="h-6 w-16" />
        <Skeleton className="h-3 w-16" />
      </div>
      <div className="flex gap-4 overflow-x-auto pb-4 px-4 lg:px-8">
        {Array.from({ length: 5 }).map((_, i) => (
          <SkeletonLane key={i} />
        ))}
      </div>
    </div>
  );
}

export function TeamSkeleton() {
  return (
    <div className="mx-auto max-w-6xl p-4 lg:p-8" data-testid="page-loading">
      <div className="flex items-center justify-between mb-6">
        <Skeleton variant="text" className="h-7 w-36" />
        <Skeleton variant="text" className="h-9 w-28 rounded-xl" />
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        {Array.from({ length: 6 }).map((_, i) => (
          <Skeleton key={i} variant="card" className="h-52 rounded-2xl" />
        ))}
      </div>
    </div>
  );
}
