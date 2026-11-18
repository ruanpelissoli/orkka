import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { TrackerConnectionsList } from "@/components/settings/TrackerConnections";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";

export default function TrackersSettingsPage() {
  const t = useTranslations("dashboard");
  const tTrackers = useTranslations("dashboard.trackers");
  useDocumentTitle(t("meta.trackers"));

  // A failed read shows the empty state with the add commands — never an error page.
  const { data: connections } = useApiQuery(
    () => api.trackers.connections("").catch(() => []),
    []
  );

  if (!connections) return <DashboardSkeleton />;

  return (
    <div className="space-y-4">
      <GlassCard padding="md" data-testid="tracker-section">
        <h3 className="font-display text-sm font-semibold text-white">{tTrackers("sectionTitle")}</h3>
        <p className="mt-1 max-w-xl text-xs text-gray-400">{tTrackers("sectionIntro")}</p>
        <div className="mt-4">
          <TrackerConnectionsList connections={connections} />
        </div>
      </GlassCard>
    </div>
  );
}
