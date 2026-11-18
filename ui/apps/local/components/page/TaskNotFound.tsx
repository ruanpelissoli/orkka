import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { GlassCard } from "@/components/ui";

/** The task pages' "no such task" card (the route segment is shown as typed). */
export function TaskNotFound({
  projectId,
  taskId,
  className = "max-w-5xl",
}: {
  projectId: string;
  taskId: string;
  className?: string;
}) {
  const t = useTranslations("dashboard");
  return (
    <div className={`mx-auto ${className} p-4 lg:p-8`}>
      <GlassCard padding="lg">
        <div className="text-center py-8">
          <h2 className="font-display text-lg font-semibold text-white">
            {t("reviewPage.taskNotFound")}
          </h2>
          <p className="mt-2 text-sm text-gray-400 font-mono">{taskId}</p>
          <Link
            href={`/p/${projectId}/board`}
            className="mt-4 inline-block text-sm text-primary-400 hover:text-primary-300"
          >
            {t("mergePage.backToBoard")}
          </Link>
        </div>
      </GlassCard>
    </div>
  );
}
