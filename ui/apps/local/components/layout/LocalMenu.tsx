import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { Dropdown, NavIcon } from "@/components/ui";

/** Where the account menu used to be: there is no account, just this machine's settings. */
const ITEMS = [
  { key: "/settings/workspace", label: "workspace" },
  { key: "/settings/machine", label: "machine" },
  { key: "/settings/trackers", label: "trackers" },
  { key: "/settings/api-keys", label: "mcpKeys" },
  { key: "/settings/preferences", label: "preferences" },
] as const;

function LocalMenu() {
  const t = useTranslations("dashboard.settingsNav");
  const router = useRouter();

  return (
    <Dropdown
      align="right"
      onSelect={(href) => router.push(href)}
      items={ITEMS.map((item) => ({ key: item.key, label: t(item.label) }))}
      trigger={
        <span
          className="flex h-8 w-8 items-center justify-center rounded-full border border-white/[0.08] bg-white/[0.04] text-gray-300 transition-colors hover:text-white"
          aria-label={t("title")}
          title={t("title")}
          data-testid="local-menu"
        >
          <NavIcon name="settings" />
        </span>
      }
    />
  );
}

export { LocalMenu };
