import { useTranslations } from "use-intl";
import { TrackerKindIcon } from "@/components/ui/brand-icons";
import { formatRelativeTime } from "@/lib/utils";
import type { ProjectTrackerConfig } from "@/lib/types";

/**
 * One quiet line under the board header when the project takes tickets from a tracker:
 * where they come from and whether the owner's runner is actually checking. A failed check
 * is the one state worth colour — tickets silently stop arriving otherwise.
 */
export function TicketSourceStatusLine({ config }: { config: ProjectTrackerConfig | null }) {
  const t = useTranslations("dashboard.ticketSourceStatus");
  const tKind = useTranslations("dashboard.trackers.kind");
  if (!config) return null;

  const kind = config.connection?.kind === "ado" ? "ado" : "jira";
  const tracker = tKind(kind);
  const machine = config.connection?.machineName ?? null;

  let text: string;
  let tone = "text-gray-400";
  if (!config.enabled) {
    text = t("paused", { tracker });
  } else if (config.lastPollError) {
    text = t("error", { tracker, error: config.lastPollError });
    tone = "text-amber-400";
  } else if (config.lastPolledAt) {
    const when = formatRelativeTime(config.lastPolledAt);
    text = machine ? t("checked", { tracker, when, machine }) : t("checkedNoMachine", { tracker, when });
  } else {
    text = t("notPolled", { tracker });
  }

  return (
    <p
      className={`flex min-w-0 items-center gap-1.5 text-xs ${tone}`}
      data-testid="ticket-source-status"
    >
      <TrackerKindIcon kind={kind} size={12} className="shrink-0" />
      <span className="min-w-0 truncate" title={text}>
        {text}
      </span>
    </p>
  );
}
