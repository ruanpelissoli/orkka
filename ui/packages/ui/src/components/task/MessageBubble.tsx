import { useState, type ReactNode } from "react";
import { useTranslations } from "use-intl";
import { cn, normalizeMessageType, parseMessageMetadata } from "@/lib/utils";
import { Avatar, Button, Markdown } from "@/components/ui";
import { TrackerKindIcon } from "@/components/ui/brand-icons";
import { trackerKindFromComment } from "@/lib/external-ticket";
import { Link } from "@/i18n/navigation";
import { formatRelativeTime, formatUsd, formatUsdEstimate } from "@/lib/utils";
import {
  extractQuestions,
  QuestionsCard,
  parseQuestionAnswers,
  QuestionAnswers,
} from "@/components/chat/QuestionBlocks";
import type { TaskMessage } from "@/lib/types";

interface MessageBubbleProps {
  message: TaskMessage;
  /** True only for the latest message while the task awaits an answer — question cards accept input */
  interactive?: boolean;
  onQuestionsSubmit?: (text: string) => void;
  /** Continue / Stop on a spend checkpoint question (metadata.spendCheckpoint). */
  onSpendDecision?: (decision: "continue" | "stop") => void | Promise<void>;
  /** Route ref for in-app links (orkka-local://pull/N PR URLs resolve to the task's review page). */
  projectId?: string;
  /** Friendly task number for the in-app PR review route. */
  taskNumber?: number;
}

// Matches web URLs plus the local-folder repos' PR scheme (orkka-local://pull/N).
const URL_PATTERN = /(https?:\/\/[^\s<>"]+|orkka-local:\/\/[^\s<>"]+)/g;
const LINK_CLASS = "text-cyan-400 underline decoration-cyan-400/40 underline-offset-2 hover:text-cyan-300 break-all";

/**
 * Renders message text with clickable links. Plain https? URLs open in a new tab;
 * `orkka-local://pull/N` — a scheme no browser can open — is rewritten to the
 * task's in-app PR review page when the routing context is available.
 */
function linkifyContent(content: string, projectId?: string, taskNumber?: number): ReactNode[] {
  const nodes: ReactNode[] = [];
  let last = 0;
  for (const match of content.matchAll(URL_PATTERN)) {
    const url = match[0];
    // Trailing punctuation belongs to the prose, not the URL.
    const trimmed = url.replace(/[.,;:!?)\]]+$/, "");
    const index = match.index!;
    if (index > last) nodes.push(content.slice(last, index));

    const localPr = trimmed.match(/^orkka-local:\/\/pull\/(\d+)$/);
    if (localPr && projectId && taskNumber != null) {
      nodes.push(
        <Link key={index} href={`/p/${projectId}/tasks/${taskNumber}/review`} className={LINK_CLASS}>
          PR #{localPr[1]}
        </Link>
      );
    } else if (trimmed.startsWith("orkka-local://")) {
      // No route context — at least don't render a dead link.
      nodes.push(trimmed);
    } else {
      nodes.push(
        <a key={index} href={trimmed} target="_blank" rel="noopener noreferrer" className={LINK_CLASS}>
          {trimmed}
        </a>
      );
    }
    last = index + trimmed.length;
  }
  if (last < content.length) nodes.push(content.slice(last));
  return nodes;
}

/**
 * Prepares agent-authored content for markdown rendering: rewrites the local-folder
 * repos' `orkka-local://pull/N` scheme (which no browser can open) into a
 * markdown link to the task's in-app PR review page. Bare https? URLs need no
 * handling — GFM autolinks them.
 */
function toAgentMarkdown(content: string, projectId?: string, taskNumber?: number): string {
  return content.replace(/orkka-local:\/\/pull\/(\d+)/g, (_, pr) =>
    projectId && taskNumber != null
      ? `[PR #${pr}](/p/${projectId}/tasks/${taskNumber}/review)`
      : `PR #${pr}`
  );
}

interface SpendCheckpointInfo {
  spentUsd: number;
  limitUsd: number | null;
  allotmentUsd: number | null;
}

function parseSpendCheckpoint(metadata: Record<string, unknown> | null): SpendCheckpointInfo | null {
  const raw = metadata?.spendCheckpoint;
  if (raw === null || typeof raw !== "object") return null;
  const cp = raw as Record<string, unknown>;
  const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? v : null);
  return {
    spentUsd: num(cp.spentUsd) ?? 0,
    limitUsd: num(cp.limitUsd),
    allotmentUsd: num(cp.allotmentUsd),
  };
}

function SpendCheckpointCard({
  checkpoint,
  interactive,
  onDecision,
}: {
  checkpoint: SpendCheckpointInfo;
  interactive: boolean;
  onDecision?: (decision: "continue" | "stop") => void | Promise<void>;
}) {
  const t = useTranslations("dashboard.taskMisc");
  const [busy, setBusy] = useState<"continue" | "stop" | null>(null);
  const canAct = interactive && !!onDecision && busy === null;

  async function decide(decision: "continue" | "stop") {
    if (!onDecision) return;
    setBusy(decision);
    try {
      await onDecision(decision);
    } finally {
      setBusy(null);
    }
  }

  // The metadata carries US dollars: the task's estimated list-price spend, never a charge.
  const spent = formatUsdEstimate(checkpoint.spentUsd);
  const body =
    checkpoint.limitUsd !== null
      ? t("spendCheckpointBody", { spent, limit: formatUsd(checkpoint.limitUsd) })
      : t("spendCheckpointBodyPlatform", { spent });

  return (
    <div className="space-y-2">
      <p className="font-semibold text-amber-300">{t("spendCheckpointTitle")}</p>
      <p className="leading-relaxed">{body}</p>
      <p className="text-xs text-gray-400">
        {checkpoint.allotmentUsd !== null
          ? t("spendCheckpointHint", { allotment: formatUsd(checkpoint.allotmentUsd) })
          : t("spendCheckpointHintNoAllotment")}
      </p>
      {interactive && (
        <div className="flex flex-wrap gap-2 pt-1">
          <Button size="sm" disabled={!canAct} loading={busy === "continue"} onClick={() => decide("continue")}>
            {t("spendCheckpointContinue")}
          </Button>
          <Button size="sm" variant="secondary" disabled={!canAct} loading={busy === "stop"} onClick={() => decide("stop")}>
            {t("spendCheckpointStop")}
          </Button>
        </div>
      )}
    </div>
  );
}

function ExternalCommentBubble({ content }: { content: string }) {
  const t = useTranslations("dashboard.externalTicket");
  const tKind = useTranslations("dashboard.trackers.kind");
  const kind = trackerKindFromComment(content);

  return (
    <div className="flex max-w-[90%] min-w-0 gap-2 lg:max-w-[85%] lg:gap-3" data-testid="external-comment">
      <span className="mt-1 flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-white/[0.08] bg-white/[0.04] text-gray-300">
        <TrackerKindIcon kind={kind ?? "jira"} size={14} />
      </span>
      <div className="min-w-0 space-y-1">
        <span className="block px-1 text-xs font-semibold text-gray-400">
          {kind ? t("fromTracker", { tracker: tKind(kind) }) : t("fromTrackerGeneric")}
        </span>
        <div className="rounded-2xl rounded-tl-md border border-dashed border-white/[0.1] bg-white/[0.02] px-3 py-2 text-sm text-gray-300 lg:px-4 lg:py-2.5">
          <Markdown className="text-inherit">{content}</Markdown>
        </div>
      </div>
    </div>
  );
}

const messageTypeStyles: Record<string, string> = {
  question: "border-l-2 border-l-amber-500/50 bg-amber-500/[0.03]",
  answer: "border-l-2 border-l-emerald-500/50 bg-emerald-500/[0.03]",
  error: "border-l-2 border-l-red-500/50 bg-red-500/[0.03]",
  progress: "opacity-70",
  plan: "border border-violet-500/20 bg-violet-500/[0.03]",
  code_review: "border border-cyan-500/20 bg-cyan-500/[0.03]",
  qa_report: "border border-amber-500/20 bg-amber-500/[0.03]",
};

function MessageBubble({ message, interactive = false, onQuestionsSubmit, onSpendDecision, projectId, taskNumber }: MessageBubbleProps) {
  const t = useTranslations("dashboard.taskMisc");
  const isHuman = message.role === "user";
  const isSystem = message.role === "system";
  const messageType = normalizeMessageType(message.messageType);
  const structuredQuestions =
    !isHuman && messageType === "question" ? extractQuestions(message.content) : null;
  // A reply submitted from a QuestionsCard — show it as question/answer pairs, not markdown source
  const humanQuestionAnswers = isHuman ? parseQuestionAnswers(message.content) : null;
  const metadata = parseMessageMetadata(message.metadata);
  // The platform parked the task at its spend limit: render the localized card with
  // Continue / Stop instead of the agent-language fallback text the message carries.
  const spendCheckpoint =
    !isHuman && messageType === "question" ? parseSpendCheckpoint(metadata) : null;
  const agentName =
    !isHuman && typeof metadata?.agentName === "string"
      ? metadata.agentName
      : null;
  const agentRole =
    !isHuman && typeof metadata?.agentRole === "string"
      ? metadata.agentRole
      : null;

  // A comment copied from the task's Jira / Azure DevOps ticket. Not the agent and not the
  // user, so it gets its own look: the tracker's icon and where it came from, then the
  // comment's markdown as the tracker had it.
  if (message.role === "tracker" || messageType === "external_comment") {
    return (
      <ExternalCommentBubble content={message.content} />
    );
  }

  if (isSystem) {
    return (
      <div className="flex justify-center py-2">
        <div className="min-w-0 max-w-[90%] rounded-2xl bg-white/[0.04] px-3 py-1">
          <Markdown className="text-xs text-gray-400 space-y-1.5">
            {message.content}
          </Markdown>
        </div>
      </div>
    );
  }

  return (
    <div
      className={cn(
        "flex gap-2 lg:gap-3 max-w-[90%] lg:max-w-[85%] min-w-0",
        isHuman ? "ml-auto flex-row-reverse" : ""
      )}
    >
      {/* Avatar */}
      <Avatar
        name={isHuman ? "You" : agentName ?? "Agent"}
        size="sm"
        className="mt-1 shrink-0"
      />

      {/* Bubble — min-w-0 lets the flex item shrink below its min-content width,
          so long unbreakable tokens (test names, paths, inline code) wrap instead
          of widening the bubble past the modal's overflow-x-hidden clip. */}
      <div className="min-w-0 space-y-1">
        {agentName && (
          <span className="block px-1 text-xs font-semibold text-gray-400">
            {agentName}
            {agentRole && <span className="text-gray-400"> · {agentRole}</span>}
          </span>
        )}
        <div
          className={cn(
            "rounded-2xl px-3 py-2 lg:px-4 lg:py-2.5 text-sm",
            isHuman
              ? "rounded-tr-md bg-primary-600/20 text-gray-200"
              : "rounded-tl-md bg-white/[0.04] text-gray-300",
            messageTypeStyles[messageType]
          )}
        >
          {/* Question label */}
          {messageType === "question" && (
            <div className="mb-1.5 flex items-center gap-1.5 text-label text-amber-400">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <circle cx="12" cy="12" r="10" />
                <path d="M9.09 9a3 3 0 015.83 1c0 2-3 3-3 3M12 17h.01" strokeLinecap="round" />
              </svg>
              {t("messageQuestion")}
            </div>
          )}

          {/* Code review label */}
          {messageType === "code_review" && (
            <div className="mb-1.5 flex items-center gap-1.5 text-label text-cyan-400">
              <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                <circle cx="5" cy="3.5" r="1.5" />
                <circle cx="5" cy="12.5" r="1.5" />
                <circle cx="11" cy="12.5" r="1.5" />
                <path d="M5 5v6M11 11V6.5a2 2 0 00-2-2H7" strokeLinecap="round" />
              </svg>
              {t("messageCodeReview")}
            </div>
          )}

          {/* QA report label */}
          {messageType === "qa_report" && (
            <div className="mb-1.5 flex items-center gap-1.5 text-label text-amber-400">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M9 12l2 2 4-4" strokeLinecap="round" strokeLinejoin="round" />
                <path d="M12 3l7 4v5c0 4.5-3 7.5-7 9-4-1.5-7-4.5-7-9V7l7-4z" strokeLinejoin="round" />
              </svg>
              {t("messageQaReport")}
            </div>
          )}

          {/* Progress dots */}
          {messageType === "progress" && (
            <div className="mb-1 flex items-center gap-1">
              <span className="h-1 w-1 animate-bounce rounded-full bg-gray-400 [animation-delay:0ms]" />
              <span className="h-1 w-1 animate-bounce rounded-full bg-gray-400 [animation-delay:150ms]" />
              <span className="h-1 w-1 animate-bounce rounded-full bg-gray-400 [animation-delay:300ms]" />
            </div>
          )}

          {spendCheckpoint ? (
            <SpendCheckpointCard
              checkpoint={spendCheckpoint}
              interactive={interactive}
              onDecision={onSpendDecision}
            />
          ) : structuredQuestions ? (
            <>
              {structuredQuestions.intro && (
                <Markdown className="text-inherit">
                  {toAgentMarkdown(structuredQuestions.intro, projectId, taskNumber)}
                </Markdown>
              )}
              <QuestionsCard
                questions={structuredQuestions.questions}
                interactive={interactive}
                onSubmit={onQuestionsSubmit}
              />
            </>
          ) : isHuman ? (
            humanQuestionAnswers ? (
              <QuestionAnswers answers={humanQuestionAnswers} />
            ) : (
              <p className="whitespace-pre-wrap break-words leading-relaxed">{linkifyContent(message.content, projectId, taskNumber)}</p>
            )
          ) : (
            <Markdown className="text-inherit">
              {toAgentMarkdown(message.content, projectId, taskNumber)}
            </Markdown>
          )}
        </div>

        <span className="block px-1 text-xs text-gray-400">
          {formatRelativeTime(message.createdAt)}
        </span>
      </div>
    </div>
  );
}

export { MessageBubble };
