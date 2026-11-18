import { useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { GlassCard, Button } from "@/components/ui";
import { CodeChangesModal } from "./CodeChangesModal";
import { useClientApi } from "@/hooks/use-client-api";
import { api } from "@/lib/api";
import type { CodeChangeSnapshotSummary } from "@/lib/types";

interface ViewCodeChangesButtonProps {
  taskId: string;
}

/**
 * Entry point to the task's permanent code-change history. Renders nothing until at least one
 * snapshot is stored — the fetch itself is the gate, which also quietly covers tasks that were
 * bounced back to Developing and tasks predating snapshot capture.
 */
function ViewCodeChangesButton({ taskId }: ViewCodeChangesButtonProps) {
  const t = useTranslations("dashboard.codeChanges");
  const withToken = useClientApi();

  const [snapshots, setSnapshots] = useState<CodeChangeSnapshotSummary[]>([]);
  const [open, setOpen] = useState(false);
  const [failed, setFailed] = useState(false);
  const [retryToken, setRetryToken] = useState(0);

  useEffect(() => {
    let cancelled = false;
    withToken((token) => api.tasks.codeChanges(taskId, token))
      .then((result) => {
        if (cancelled) return;
        setSnapshots(result);
        setFailed(false);
      })
      .catch(() => {
        // An empty list means "no history yet" and hides the card — correct. A FAILED
        // fetch is a different fact: hiding it too made a 401/500 indistinguishable from
        // "this feature doesn't exist", which is exactly how it got reported broken.
        if (!cancelled) setFailed(true);
      });
    return () => {
      cancelled = true;
    };
  }, [withToken, taskId, retryToken]);

  if (failed) {
    return (
      <GlassCard padding="md">
        <div className="flex items-center justify-between gap-3">
          <p className="text-xs text-gray-400">{t("loadFailed")}</p>
          <Button size="sm" variant="ghost" onClick={() => setRetryToken((n) => n + 1)}>
            {t("retry")}
          </Button>
        </div>
      </GlassCard>
    );
  }

  if (snapshots.length === 0) return null;

  return (
    <GlassCard padding="md" accentColor="#8b5cf6">
      <div className="flex items-center justify-between gap-3">
        <div className="min-w-0">
          <h3 className="font-display text-sm font-semibold text-white">{t("title")}</h3>
          <p className="mt-1 text-xs text-gray-400">
            {t("filesChanged", { count: snapshots[0].fileCount })} ·{" "}
            <span className="font-mono text-emerald-400">+{snapshots[0].additions}</span>{" "}
            <span className="font-mono text-red-400">-{snapshots[0].deletions}</span>
          </p>
        </div>
        <Button size="sm" variant="secondary" onClick={() => setOpen(true)}>
          <svg
            width="14"
            height="14"
            viewBox="0 0 16 16"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
          >
            <path d="M5.5 4.5L2 8l3.5 3.5M10.5 4.5L14 8l-3.5 3.5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {t("viewButton")}
        </Button>
      </div>
      {open && (
        <CodeChangesModal
          taskId={taskId}
          snapshots={snapshots}
          open={open}
          onClose={() => setOpen(false)}
        />
      )}
    </GlassCard>
  );
}

export { ViewCodeChangesButton };
export type { ViewCodeChangesButtonProps };
