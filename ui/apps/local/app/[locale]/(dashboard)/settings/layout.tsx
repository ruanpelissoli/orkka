import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { usePathname } from "@/i18n/navigation";
import { cn } from "@/lib/utils";

export default function SettingsLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const t = useTranslations("dashboard.settingsNav");
  const pathname = usePathname();

  const settingsTabs = [
    { label: t("workspace"), href: "/settings/workspace" },
    { label: t("machine"), href: "/settings/machine" },
    { label: t("trackers"), href: "/settings/trackers" },
    { label: t("usage"), href: "/settings/usage" },
    { label: t("mcpKeys"), href: "/settings/api-keys" },
    { label: t("preferences"), href: "/settings/preferences" },
    { label: t("notifications"), href: "/settings/notifications" },
  ];

  return (
    <div className="mx-auto max-w-4xl p-4 lg:p-8">
      <h1 className="font-display text-2xl font-bold text-white mb-6">
        {t("title")}
      </h1>

      {/* Tabs */}
      <div className="mb-6 flex gap-1 border-b border-white/[0.04] overflow-x-auto scrollbar-none -mb-px">
        {settingsTabs.map((tab) => {
          const isActive = pathname.startsWith(tab.href);

          return (
            <Link
              key={tab.href}
              href={tab.href}
              className={cn(
                "relative shrink-0 px-4 py-3 text-sm font-medium transition-colors",
                isActive ? "text-white" : "text-gray-400 hover:text-gray-200"
              )}
            >
              {tab.label}
              {isActive && (
                <span className="absolute inset-x-4 bottom-0 h-[2px] rounded-full bg-primary-500 shadow-[0_0_8px_rgba(59,130,246,0.4)]" />
              )}
            </Link>
          );
        })}
      </div>

      {children}
    </div>
  );
}
