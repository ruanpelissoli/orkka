import { useTranslations } from "use-intl";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import type { AgentStatsWindow } from "@/lib/types";

const WINDOWS: AgentStatsWindow[] = ["24h", "7d", "30d"];

interface StatsWindowSelectorProps {
  value: AgentStatsWindow;
  onChange: (window: AgentStatsWindow) => void;
  className?: string;
}

/** Segmented 24h / 7d / 30d control shared by the Team stats, the Activity page and the board. */
function StatsWindowSelector({ value, onChange, className }: StatsWindowSelectorProps) {
  const t = useTranslations("dashboard.teamStats");
  return (
    <SegmentedControl
      options={WINDOWS.map((w) => ({
        value: w,
        label: t(`window${w}` as "window24h" | "window7d" | "window30d"),
      }))}
      value={value}
      onChange={onChange}
      aria-label={t("windowLabel")}
      className={className}
    />
  );
}

export { StatsWindowSelector };
