import { useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { useClientApi } from "@/hooks/use-client-api";
import { api } from "@/lib/api";
import { cn, formatUsdEstimate, formatTokenCount } from "@/lib/utils";
import type { EvidenceVerdict, TaskEvidence } from "@/lib/types";

interface EvidencePanelProps {
  taskId: string;
  className?: string;
}

const VERDICT_STYLES: Record<string, { dot: string; text: string }> = {
  pass: { dot: "bg-emerald-400", text: "text-emerald-400" },
  approve: { dot: "bg-emerald-400", text: "text-emerald-400" },
  fail: { dot: "bg-red-400", text: "text-red-400" },
  request_changes: { dot: "bg-amber-400", text: "text-amber-400" },
  unknown: { dot: "bg-gray-500", text: "text-gray-400" },
};

const FILE_STATE_STYLES: Record<string, string> = {
  delivered: "text-emerald-400",
  missing: "text-red-400",
  unplanned: "text-amber-400",
};

function VerdictRow({ label, verdict }: { label: string; verdict: EvidenceVerdict }) {
  const t = useTranslations("dashboard.evidence");
  const style = VERDICT_STYLES[verdict.verdict] ?? VERDICT_STYLES.unknown;

  return (
    <div className="flex items-start gap-2.5">
      <span className={cn("mt-1.5 h-2 w-2 shrink-0 rounded-full", style.dot)} />
      <div className="min-w-0 flex-1">
        <p className="text-sm">
          <span className="text-gray-400">{label}: </span>
          <span className={cn("font-medium", style.text)}>{t(`verdicts.${verdict.verdict}`)}</span>
          {verdict.agentName && (
            <span className="ml-1.5 text-xs text-gray-400">
              {t("byAgent", { agent: verdict.agentName })}
            </span>
          )}
        </p>
        <p className="mt-1 whitespace-pre-wrap text-xs leading-relaxed text-gray-400">
          {verdict.content}
        </p>
      </div>
    </div>
  );
}

/**
 * The proof-of-done bundle. Reads top-down as an argument that the task is finished:
 * verdicts first, then promised-vs-delivered, then the runs that produced them — so the
 * diff becomes confirmation rather than the investigation itself.
 */
function EvidencePanel({ taskId, className }: EvidencePanelProps) {
  const t = useTranslations("dashboard.evidence");
  const withToken = useClientApi();

  // Keyed by task: a result for another task reads as "still loading", so switching tasks
  // needs no synchronous reset inside the effect.
  const [loaded, setLoaded] = useState<{
    taskId: string;
    evidence: TaskEvidence | null;
    failed: boolean;
  } | null>(null);

  useEffect(() => {
    let cancelled = false;

    withToken((token) => api.tasks.evidence(taskId, token))
      .then((result) => {
        if (!cancelled) setLoaded({ taskId, evidence: result, failed: false });
      })
      .catch(() => {
        if (!cancelled) setLoaded({ taskId, evidence: null, failed: true });
      });

    return () => {
      cancelled = true;
    };
  }, [withToken, taskId]);

  const current = loaded?.taskId === taskId ? loaded : null;
  const isLoading = current === null;
  const failed = current?.failed ?? false;
  const evidence = current?.evidence ?? null;

  if (isLoading) {
    return (
      <GlassCard className={className} padding="md">
        <p className="text-sm text-gray-400">{t("loading")}</p>
      </GlassCard>
    );
  }

  if (failed || !evidence) {
    return (
      <GlassCard className={className} padding="md">
        <p className="text-sm text-gray-400">{t("error")}</p>
      </GlassCard>
    );
  }

  const { totals } = evidence;
  const missingCount = evidence.fileChecklist.filter((f) => f.state === "missing").length;

  return (
    <GlassCard className={className} padding="md">
      <div className="mb-4">
        <h3 className="font-display text-sm font-semibold text-white">{t("title")}</h3>
        <p className="mt-0.5 text-xs text-gray-400">{t("subtitle")}</p>
      </div>

      <div className="space-y-3 border-b border-white/[0.05] pb-4">
        {evidence.qaVerdict ? (
          <VerdictRow label={t("qaVerdict")} verdict={evidence.qaVerdict} />
        ) : (
          <p className="text-xs text-gray-400">{t("noQa")}</p>
        )}
        {evidence.codeReview ? (
          <VerdictRow label={t("codeReview")} verdict={evidence.codeReview} />
        ) : (
          <p className="text-xs text-gray-400">{t("noReview")}</p>
        )}
      </div>

      <div className="border-b border-white/[0.05] py-4">
        <h4 className="text-label">
          {t("promisedVsDelivered")}
        </h4>

        {evidence.plan === null ? (
          <p className="mt-2 text-xs text-gray-400">{t("noPlan")}</p>
        ) : evidence.fileChecklist.length === 0 ? (
          <p className="mt-2 text-xs text-gray-400">{t("noFiles")}</p>
        ) : (
          <>
            {missingCount > 0 && (
              <p className="mt-2 text-xs text-red-400">
                {missingCount} × {t("states.missing")}
              </p>
            )}
            <ul className="mt-2 space-y-1">
              {evidence.fileChecklist.map((file) => (
                <li key={`${file.state}-${file.path}`} className="flex items-baseline gap-2 text-xs">
                  <span
                    className={cn(
                      "shrink-0 font-medium",
                      FILE_STATE_STYLES[file.state] ?? "text-gray-400"
                    )}
                  >
                    {t(`states.${file.state}`)}
                  </span>
                  <span className="truncate font-mono text-gray-400" title={file.path}>
                    {file.path}
                  </span>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>

      {evidence.attachments.length > 0 && (
        <div className="border-b border-white/[0.05] py-4">
          <h4 className="text-label">
            {t("attachments")}
          </h4>
          <div className="mt-2 flex flex-wrap gap-2">
            {evidence.attachments.map((attachment) => (
              <a
                key={attachment.id}
                href={attachment.fileUrl}
                target="_blank"
                rel="noreferrer"
                className="max-w-[160px] truncate rounded-lg bg-white/[0.04] px-2 py-1 text-xs text-gray-400 transition-colors hover:text-gray-200"
              >
                {attachment.fileName}
              </a>
            ))}
          </div>
        </div>
      )}

      <div className="grid grid-cols-2 gap-3 pt-4 sm:grid-cols-4">
        <div>
          <p className="text-xs text-gray-400">{t("tokens")}</p>
          <p className="text-sm text-gray-200">{formatTokenCount(totals.totalTokens)}</p>
        </div>
        <div>
          <p className="text-xs text-gray-400">{t("cost")}</p>
          <p className="text-sm text-gray-200">{formatUsdEstimate(totals.totalCost)}</p>
        </div>
        <div>
          <p className="text-xs text-gray-400">{t("filesTouched")}</p>
          <p className="text-sm text-gray-200">{totals.filesTouched}</p>
        </div>
        <div>
          <p className="text-xs text-gray-400">{t("cycles")}</p>
          <p className="text-sm text-gray-200">
            {t("reviewCycles", { count: totals.reviewCycleCount })} ·{" "}
            {t("qaCycles", { count: totals.qaCycleCount })}
          </p>
        </div>
      </div>
    </GlassCard>
  );
}

export { EvidencePanel };
export type { EvidencePanelProps };
