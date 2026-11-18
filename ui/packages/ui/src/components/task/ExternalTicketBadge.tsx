import { useTranslations } from "use-intl";
import { TrackerKindIcon } from "@/components/ui/brand-icons";
import { trackerKindFromUrl } from "@/lib/external-ticket";
import { cn } from "@/lib/utils";

interface ExternalTicketBadgeProps {
  externalKey: string;
  externalUrl?: string | null;
  /** "card" = compact key chip on a board card; "detail" = "SHOP-12 in Jira ↗" on the task page. */
  variant?: "card" | "detail";
  className?: string;
}

/**
 * The Jira / Azure DevOps ticket a task was imported from, linking out to it.
 *
 * On a board card the whole card is a drag handle and a click target, so the link swallows
 * its own pointer, click and key events — opening the ticket must neither open the task nor
 * start a drag.
 */
export function ExternalTicketBadge({
  externalKey,
  externalUrl,
  variant = "card",
  className,
}: ExternalTicketBadgeProps) {
  const t = useTranslations("dashboard.externalTicket");
  const tKind = useTranslations("dashboard.trackers.kind");
  const kind = trackerKindFromUrl(externalUrl);
  const trackerName = kind ? tKind(kind) : null;

  const label =
    variant === "detail" && trackerName
      ? t("inTracker", { key: externalKey, tracker: trackerName })
      : externalKey;
  const title = trackerName
    ? t("openInTracker", { key: externalKey, tracker: trackerName })
    : t("openTicket", { key: externalKey });

  const content = (
    <>
      {kind && <TrackerKindIcon kind={kind} size={variant === "card" ? 11 : 12} className="shrink-0" />}
      <span className="font-mono">{label}</span>
      {variant === "detail" && externalUrl && <span aria-hidden>↗</span>}
    </>
  );

  const classes = cn(
    "inline-flex items-center gap-1 rounded-full border border-white/[0.08] bg-white/[0.04] text-gray-300",
    variant === "card" ? "px-1.5 py-0.5 text-xs" : "px-2.5 py-0.5 text-xs font-medium",
    className
  );

  if (!externalUrl) {
    return (
      <span className={classes} data-testid="external-ticket-badge">
        {content}
      </span>
    );
  }

  return (
    <a
      href={externalUrl}
      target="_blank"
      rel="noopener noreferrer"
      title={title}
      data-testid="external-ticket-badge"
      className={cn(classes, "transition-colors hover:bg-white/[0.08] hover:text-white")}
      onClick={(e) => e.stopPropagation()}
      onPointerDown={(e) => e.stopPropagation()}
      onKeyDown={(e) => e.stopPropagation()}
    >
      {content}
    </a>
  );
}
