import { useState } from "react";
import { useTranslations } from "use-intl";
import { Toggle } from "@/components/ui/Toggle";
import {
  getDesktopNotificationPermission,
  getDesktopNotificationsEnabled,
  isDesktopNotificationsSupported,
  requestDesktopNotificationPermission,
  setDesktopNotificationsEnabled,
} from "@/lib/desktop-notifications";

interface DesktopNotificationsToggleProps {
  /** Compact rendering for the bell dropdown footer (no description line). */
  compact?: boolean;
}

/**
 * Per-device opt-in for OS toasts. Enabling requests the browser permission;
 * a denied permission surfaces a hint instead of a silently broken toggle.
 */
function DesktopNotificationsToggle({ compact }: DesktopNotificationsToggleProps) {
  const t = useTranslations("dashboard");
  // Browser state read once on mount — the app is client-rendered only, so it is there on the
  // first render.
  const [enabled, setEnabled] = useState(() => getDesktopNotificationsEnabled());
  const [permission, setPermission] = useState<NotificationPermission | null>(() =>
    getDesktopNotificationPermission()
  );
  const [supported] = useState(() => isDesktopNotificationsSupported());

  const handleChange = async (next: boolean) => {
    if (next) {
      const result = await requestDesktopNotificationPermission();
      setPermission(result);
      if (result !== "granted") {
        setDesktopNotificationsEnabled(false);
        setEnabled(false);
        return;
      }
    }
    setDesktopNotificationsEnabled(next);
    setEnabled(next);
  };

  const blocked = permission === "denied";

  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0">
        <p className={compact ? "text-xs text-gray-300" : "text-sm font-medium text-white"}>
          {t("notifications.desktop.label")}
        </p>
        {!compact && (
          <p className="mt-0.5 text-xs text-gray-400">
            {t("notifications.desktop.description")}
          </p>
        )}
        {!supported && (
          <p className="mt-0.5 text-xs text-amber-400/80">
            {t("notifications.desktop.unsupported")}
          </p>
        )}
        {supported && blocked && (
          <p className="mt-0.5 text-xs text-amber-400/80">
            {t("notifications.desktop.blocked")}
          </p>
        )}
      </div>
      <Toggle
        checked={enabled && permission === "granted"}
        onChange={(next) => void handleChange(next)}
        disabled={!supported || blocked}
      />
    </div>
  );
}

export { DesktopNotificationsToggle };
