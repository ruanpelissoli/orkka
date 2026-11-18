import { useTranslations } from "use-intl";
import { cn, formatRelativeTime, truncate } from "@/lib/utils";
import { InboxKindBadge, inboxKindColor } from "./InboxKindBadge";
import type { InboxItem } from "@/lib/types";

interface InboxItemRowProps {
  item: InboxItem;
  /** Keyboard cursor position — visually distinct from hover so both can be shown at once. */
  isActive?: boolean;
  onSelect: (item: InboxItem) => void;
  onHover?: (item: InboxItem) => void;
}

/**
 * One row of the inbox. Leads with the action ("Approve plan"), not the task title: the
 * question the reader is answering is "what do I have to do", and the title only
 * disambiguates between items once that is known.
 */
function InboxItemRow({ item, isActive = false, onSelect, onHover }: InboxItemRowProps) {
  const t = useTranslations("dashboard.inbox");
  const accent = inboxKindColor(item.kind);

  return (
    <li>
      <button
        type="button"
        onClick={() => onSelect(item)}
        onMouseEnter={() => onHover?.(item)}
        aria-current={isActive ? "true" : undefined}
        className={cn(
          "flex w-full items-start gap-3 px-4 py-3 text-left transition-colors",
          "hover:bg-white/[0.04] focus:bg-white/[0.06] focus:outline-none",
          isActive && "bg-white/[0.05]"
        )}
      >
        <span
          aria-hidden
          className="mt-1 h-8 w-0.5 shrink-0 rounded-full"
          style={{ backgroundColor: accent, opacity: isActive ? 1 : 0.5 }}
        />

        <span className="min-w-0 flex-1">
          <span className="flex flex-wrap items-center gap-2">
            <InboxKindBadge kind={item.kind} />
            <span className="font-mono text-xs text-gray-400">{item.code}</span>
            <span className="truncate text-xs text-gray-400">{item.projectName}</span>
            {item.repoName && (
              <span className="truncate text-xs text-gray-400">· {item.repoName}</span>
            )}
          </span>

          <span className="mt-1 block truncate text-sm font-medium text-gray-100">
            {item.title}
          </span>

          {item.preview && (
            <span className="mt-0.5 block truncate text-xs text-gray-400">
              {item.agentName ? `${item.agentName}: ` : ""}
              {truncate(item.preview, 140)}
            </span>
          )}
        </span>

        <span className="flex shrink-0 flex-col items-end gap-1 pt-0.5">
          <span className="text-xs text-gray-400">
            {t("waiting", { time: formatRelativeTime(item.waitingSince) })}
          </span>
          {item.blockedDependents > 0 && (
            <span className="rounded-full bg-amber-500/10 px-2 py-0.5 text-xs font-medium text-amber-400">
              {t("blockedDependents", { count: item.blockedDependents })}
            </span>
          )}
        </span>
      </button>
    </li>
  );
}

export { InboxItemRow };
export type { InboxItemRowProps };
