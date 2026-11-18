import { useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { GlassCard } from "@/components/ui";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import type { TaskDocument } from "@/lib/types";

interface TaskDocumentationCardProps {
  taskId: string;
  projectId: string;
}

/**
 * Documents this task's work caused — the output side of documentation, where
 * TaskDocumentsCard is the input side (documents attached to the task's context).
 *
 * Renders nothing at all until there is something to show. Most tasks never produce a
 * document, and an empty "Documentation" card on every task page would be pure noise.
 */
function TaskDocumentationCard({ taskId, projectId }: TaskDocumentationCardProps) {
  const t = useTranslations("dashboard.taskDocumentation");
  const withToken = useClientApi();
  const [documents, setDocuments] = useState<TaskDocument[]>([]);

  useEffect(() => {
    let cancelled = false;
    withToken((token) => api.documents.byTask(projectId, taskId, token))
      .then((docs) => {
        if (!cancelled) setDocuments(docs);
      })
      .catch(() => {
        // The card is supplementary — a failed fetch stays silent rather than
        // putting an error where the user expects information.
        if (!cancelled) setDocuments([]);
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, taskId, withToken]);

  if (documents.length === 0) return null;

  return (
    <GlassCard padding="md">
      <h4 className="mb-3 text-label">
        {t("title")}
      </h4>

      <div className="space-y-1">
        {documents.map((doc) => {
          const created = doc.outcome === "Created";
          return (
            <Link
              key={doc.id}
              href={`/p/${projectId}/documents?d=${doc.id}`}
              className="group flex items-center gap-2 rounded-lg px-2 py-1.5 transition-colors hover:bg-white/[0.03]"
              title={doc.path ? `${doc.path}/${doc.title}` : doc.title}
            >
              <svg
                width="14"
                height="14"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.5"
                className="shrink-0 text-amber-400/80"
              >
                <path
                  d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
                <path
                  d="M14 2v6h6M16 13H8M16 17H8"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
              </svg>
              <span className="min-w-0 flex-1">
                <span className="block truncate text-xs text-gray-300 transition-colors group-hover:text-white">
                  {doc.title}
                </span>
                {doc.path && (
                  <span className="block truncate font-mono text-xs text-gray-400">
                    {doc.path}
                  </span>
                )}
              </span>
              <span
                className={cn(
                  "shrink-0 rounded px-1.5 py-0.5 text-xs font-medium",
                  created
                    ? "bg-emerald-500/10 text-emerald-400"
                    : "bg-cyan-500/10 text-cyan-400"
                )}
              >
                {created ? t("created") : `${t("updated")} · v${doc.version}`}
              </span>
            </Link>
          );
        })}
      </div>

      <p className="mt-3 text-xs leading-relaxed text-gray-400">{t("hint")}</p>
    </GlassCard>
  );
}

export { TaskDocumentationCard };
