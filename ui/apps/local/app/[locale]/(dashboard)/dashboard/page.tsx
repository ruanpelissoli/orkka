import { useEffect } from "react";
import { useTranslations } from "use-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { Button, EmptyState } from "@/components/ui";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { api } from "@/lib/api";

/** Written by ProjectProvider whenever a project is open (the slug; older cookies hold a ULID). */
const LAST_PROJECT_COOKIE = "orkka-last-project";

function lastProjectRef(): string | undefined {
  return document.cookie
    .split("; ")
    .find((c) => c.startsWith(`${LAST_PROJECT_COOKIE}=`))
    ?.split("=")[1];
}

/** The dashboard opens the last project (or the first); with none, the first-run state. */
export default function DashboardPage() {
  const t = useTranslations("dashboard.firstRun");
  const router = useRouter();
  const { data: projects } = useApiQuery(
    // API not available — show the first-run state rather than an error page.
    () => api.projects.list("").catch((): { id: string; slug: string }[] => []),
    []
  );

  const target =
    projects && projects.length > 0
      ? (projects.find((p) => p.slug === lastProjectRef() || p.id === lastProjectRef()) ??
        projects[0])
      : null;

  useEffect(() => {
    if (target) router.replace(`/p/${target.slug}`);
  }, [router, target]);

  if (!projects || target) return <DashboardSkeleton />;

  return (
    <div className="flex flex-1 items-center justify-center p-4 lg:p-8">
      <EmptyState
        title={t("title")}
        description={t("description")}
        action={
          <Link href="/projects/new">
            <Button>{t("createProject")}</Button>
          </Link>
        }
      />
    </div>
  );
}
