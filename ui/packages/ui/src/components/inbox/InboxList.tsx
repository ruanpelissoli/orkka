import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { InboxItemRow } from "./InboxItemRow";
import { InboxKindBadge } from "./InboxKindBadge";
import type { InboxCount, InboxItem } from "@/lib/types";

interface InboxListProps {
  items: InboxItem[];
  counts: InboxCount[];
  isLoading: boolean;
  error: string | null;
  activeKind: string | null;
  onKindChange: (kind: string | null) => void;
  onSelect: (item: InboxItem) => void;
  onRefresh: () => void;
  className?: string;
}

/**
 * The ranked queue. Keyboard-first on purpose: draining an inbox is a repetitive motion,
 * and reaching for the mouse between every item is what makes reviewing agent output feel
 * like a chore.
 */
function InboxList({
  items,
  counts,
  isLoading,
  error,
  activeKind,
  onKindChange,
  onSelect,
  onRefresh,
  className,
}: InboxListProps) {
  const t = useTranslations("dashboard.inbox");
  const [cursor, setCursor] = useState(0);

  // The list re-ranks under the cursor as agents finish; clamp rather than reset so the
  // reader doesn't lose their place mid-triage. Adjusted during render when the length changes.
  const [prevLength, setPrevLength] = useState(items.length);
  if (items.length !== prevLength) {
    setPrevLength(items.length);
    setCursor((prev) => (items.length === 0 ? 0 : Math.min(prev, items.length - 1)));
  }

  const handleKeyDown = useCallback(
    (event: KeyboardEvent) => {
      // Never steal keys from a field the user is typing in.
      const target = event.target as HTMLElement | null;
      if (target && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName))) {
        return;
      }
      if (event.metaKey || event.ctrlKey || event.altKey) return;

      if (event.key === "ArrowDown" || event.key === "j") {
        event.preventDefault();
        setCursor((prev) => Math.min(prev + 1, Math.max(items.length - 1, 0)));
      } else if (event.key === "ArrowUp" || event.key === "k") {
        event.preventDefault();
        setCursor((prev) => Math.max(prev - 1, 0));
      } else if (event.key === "Enter") {
        const item = items[cursor];
        if (item) {
          event.preventDefault();
          onSelect(item);
        }
      } else if (event.key === "r" || event.key === "R") {
        event.preventDefault();
        onRefresh();
      }
    },
    [items, cursor, onSelect, onRefresh]
  );

  useEffect(() => {
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [handleKeyDown]);

  if (isLoading) {
    return <div className="px-4 py-10 text-center text-sm text-gray-400">{t("loading")}</div>;
  }

  if (error) {
    return (
      <div className="px-4 py-10 text-center">
        <p className="text-sm text-gray-300">{t("error")}</p>
        <button
          type="button"
          onClick={onRefresh}
          className="mt-2 text-sm text-primary-400 transition-colors hover:text-primary-300"
        >
          {t("refresh")}
        </button>
      </div>
    );
  }

  return (
    <div className={className}>
      {counts.length > 0 && (
        <div className="flex flex-wrap items-center gap-2 border-b border-white/[0.04] px-4 py-3">
          <button
            type="button"
            onClick={() => onKindChange(null)}
            className={cn(
              "rounded-full px-2.5 py-0.5 text-xs font-medium transition-colors",
              activeKind === null
                ? "bg-white/[0.12] text-gray-100"
                : "bg-white/[0.04] text-gray-400 hover:text-gray-200"
            )}
          >
            {t("filterAll")}
          </button>
          {counts.map((count) => (
            <button
              key={count.kind}
              type="button"
              onClick={() => onKindChange(activeKind === count.kind ? null : count.kind)}
              className={cn(
                "flex items-center gap-1.5 rounded-full transition-opacity",
                activeKind !== null && activeKind !== count.kind && "opacity-50 hover:opacity-100"
              )}
            >
              <InboxKindBadge kind={count.kind} />
              <span className="text-xs text-gray-400">{count.count}</span>
            </button>
          ))}
        </div>
      )}

      {items.length === 0 ? (
        <div className="px-4 py-12 text-center">
          <p className="text-sm font-medium text-gray-300">{t("empty")}</p>
          <p className="mt-1 text-xs text-gray-400">{t("emptyDescription")}</p>
        </div>
      ) : (
        <>
          <ul className="divide-y divide-white/[0.03]">
            {items.map((item, index) => (
              <InboxItemRow
                key={item.taskId}
                item={item}
                isActive={index === cursor}
                onSelect={onSelect}
                onHover={() => setCursor(index)}
              />
            ))}
          </ul>
          <p className="px-4 py-3 text-xs text-gray-400">{t("shortcutHint")}</p>
        </>
      )}
    </div>
  );
}

export { InboxList };
export type { InboxListProps };
