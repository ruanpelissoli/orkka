import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";

/**
 * Colour per action kind. Shared by the badge and the row's accent bar so a kind is
 * recognisable before the label is read — the inbox is meant to be scanned, not studied.
 */
const KIND_COLORS: Record<string, string> = {
  plan_approval: "#a78bfa",
  question: "#f59e0b",
  code_review: "#06b6d4",
  qa_review: "#fbbf24",
  verification: "#22d3ee",
  merge_conflict: "#fb923c",
  merge_failed: "#ef4444",
  blocked: "#9ca3af",
  runner_offline: "#f59e0b",
  out_of_tokens: "#ef4444",
  error: "#ef4444",
};

const FALLBACK_COLOR = "#6b7280";

function inboxKindColor(kind: string): string {
  return KIND_COLORS[kind] ?? FALLBACK_COLOR;
}

interface InboxKindBadgeProps {
  kind: string;
  className?: string;
}

function InboxKindBadge({ kind, className }: InboxKindBadgeProps) {
  const t = useTranslations("dashboard.inbox");
  const color = inboxKindColor(kind);
  // An unrecognised kind from a newer API still renders — as "needs attention".
  const label = kind in KIND_COLORS ? t(`kinds.${kind}`) : t("kinds.unknown");

  return (
    <span
      className={cn(
        "inline-flex shrink-0 items-center rounded-full px-2 py-0.5 text-xs font-medium",
        className
      )}
      style={{ backgroundColor: `${color}1f`, color }}
    >
      {label}
    </span>
  );
}

export { InboxKindBadge, inboxKindColor };
export type { InboxKindBadgeProps };
