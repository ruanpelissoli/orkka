import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { DesktopNotificationsToggle } from "@/components/notifications/DesktopNotificationsToggle";
import { useDocumentTitle } from "@/hooks/use-document-title";

export default function NotificationPrefsPage() {
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.notificationPreferences"));

  return (
    <div className="space-y-6">
      <div>
        <h2 className="font-display text-lg font-semibold text-white">
          {t("notifications.settingsTitle")}
        </h2>
        <p className="mt-1 text-sm text-gray-400">
          {t("notifications.settingsDescription")}
        </p>
      </div>

      <GlassCard padding="md">
        <DesktopNotificationsToggle />
      </GlassCard>
    </div>
  );
}
