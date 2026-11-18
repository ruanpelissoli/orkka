import { useCallback, useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { GlassCard, Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { useTaskSignalR } from "@/hooks/use-task-signalr";
import { decideApproval } from "@/lib/actions";
import type { PendingApproval } from "@/lib/types";

const WAITING_ACCENT = "#f59e0b";

type StreamEvent = NonNullable<ReturnType<typeof useTaskSignalR>["streamEvents"]>[number];

/** The approval cards after a run of stream events: requests add one, settlements remove it. */
function applyApprovalEvents(
  current: PendingApproval[],
  events: StreamEvent[],
  taskId: string
): PendingApproval[] {
  let next = current;
  for (const event of events) {
    const meta = event.metadata as Record<string, string> | undefined;
    const approvalId = meta?.approvalId;
    if (!approvalId) continue;

    if (event.type === "ApprovalRequested") {
      // The server may re-raise the same tool call after a reconnect; one card per prompt.
      if (!next.some((a) => a.id === approvalId)) {
        next = [
          ...next,
          {
            id: approvalId,
            taskId,
            toolUseId: meta.toolUseId ?? "",
            tool: meta.tool ?? "Bash",
            command: event.content,
            approvalKey: meta.approvalKey ?? "",
            createdAt: event.timestamp,
          },
        ];
      }
    } else if (event.type === "ApprovalSettled") {
      // Covers expiry and another viewer's click as well as our own answer.
      next = next.filter((a) => a.id !== approvalId);
    }
  }
  return next;
}

/**
 * Commands an interactive session has paused on, and the controls that release them.
 *
 * The agent is genuinely blocked while a card is on screen — its Bash hook is waiting on this
 * answer — so the card leads with the command, verbatim and unwrapped. A paraphrased or truncated
 * command is not the thing being approved, and this is the one place in the product where reading
 * the exact text is the entire point.
 */
export function InteractiveApprovalPanel({
  taskId,
  initialApprovals,
  className,
}: {
  taskId: string;
  initialApprovals: PendingApproval[];
  className?: string;
}) {
  const t = useTranslations("dashboard.interactiveSession");
  const [approvals, setApprovals] = useState<PendingApproval[]>(initialApprovals);
  const [error, setError] = useState<string | null>(null);
  const [pending, startTransition] = useTransition();

  // Shares the task's existing hub connection (SignalRProvider), so subscribing here costs
  // nothing beyond the handler.
  const { streamEvents } = useTaskSignalR({ taskId });

  // Cursor over the stream, not a peek at the last entry: React batches state updates, so
  // several stream chunks can land in one commit — an ApprovalRequested followed by any
  // other chunk used to be invisible, and the card never rendered. New chunks are folded into
  // the cards during render (the cursor is state), not in an effect.
  //
  // Defaulted rather than assumed: this panel hangs off the task page, and a hook that hands
  // back nothing (a stubbed provider, a connection that never came up) must make the panel
  // inert — not throw during render and take the whole task detail down with it.
  const events = streamEvents ?? [];
  const [processedCount, setProcessedCount] = useState(0);
  if (events.length !== processedCount) {
    // The stream resets to [] on a lane change — resync the cursor with it.
    const from = events.length < processedCount ? 0 : processedCount;
    const fresh = events.slice(from);
    setProcessedCount(events.length);
    setApprovals((current) => applyApprovalEvents(current, fresh, taskId));
  }

  const decide = useCallback(
    (approvalId: string, decision: "allow" | "allow_session" | "deny") => {
      setError(null);
      // Removed optimistically: the agent is waiting, and leaving a dead card on screen invites
      // a second click. The settled event confirms it; an error puts it back.
      const removed = approvals.find((a) => a.id === approvalId);
      setApprovals((current) => current.filter((a) => a.id !== approvalId));

      startTransition(async () => {
        const result = await decideApproval(taskId, approvalId, decision);
        if (result.error) {
          setError(result.error);
          if (removed) setApprovals((current) => [...current, removed]);
        }
      });
    },
    [approvals, taskId]
  );

  if (approvals.length === 0 && !error) return null;

  return (
    <div className={className}>
      {error && (
        <p className="mb-2 text-xs text-red-400" role="alert">
          {error}
        </p>
      )}

      {approvals.map((approval) => (
        <GlassCard key={approval.id} padding="md" accentColor={WAITING_ACCENT} className="mb-2">
          <div className="flex items-center gap-2">
            <span
              className="inline-block h-2 w-2 shrink-0 animate-pulse rounded-full"
              style={{ backgroundColor: WAITING_ACCENT }}
              aria-hidden="true"
            />
            <h3 className="font-display text-sm font-semibold text-white">
              {t("approvalTitle")}
            </h3>
          </div>

          <pre className="mt-2 overflow-x-auto rounded-lg bg-black/40 px-3 py-2 text-xs text-gray-100">
            {approval.command}
          </pre>

          <p className="mt-2 text-xs text-gray-400">{t("approvalHint")}</p>

          <RequirePermission permission="TasksExecute">
            <div className="mt-3 flex flex-wrap gap-2">
              <Button size="sm" disabled={pending} onClick={() => decide(approval.id, "allow")}>
                {t("approvalAllow")}
              </Button>
              {approval.approvalKey && (
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={pending}
                  onClick={() => decide(approval.id, "allow_session")}
                >
                  {/* Names the exact grant — a permission whose shape the user cannot see is
                      not a choice they are making. */}
                  {t("approvalAllowAlways", { key: approval.approvalKey })}
                </Button>
              )}
              <Button
                size="sm"
                variant="secondary"
                disabled={pending}
                onClick={() => decide(approval.id, "deny")}
              >
                {t("approvalDeny")}
              </Button>
            </div>
          </RequirePermission>
        </GlassCard>
      ))}
    </div>
  );
}
