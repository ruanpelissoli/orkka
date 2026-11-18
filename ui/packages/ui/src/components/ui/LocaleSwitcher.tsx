import { useLocale, useTranslations } from "use-intl";
import { usePathname, useRouter } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";
import { useUserPreferencesContext } from "@/components/providers/UserPreferencesProvider";
import { cn } from "@/lib/utils";

const localeFlags: Record<string, string> = {
  en: "EN",
  es: "ES",
  pt: "PT",
  fr: "FR",
  de: "DE",
};

export function LocaleSwitcher({ fullWidth = false }: { fullWidth?: boolean }) {
  const locale = useLocale();
  const router = useRouter();
  const pathname = usePathname();
  const t = useTranslations("common");
  // No-op outside the dashboard (marketing pages render this without the provider), which
  // is correct: there is no member to save a preference for until someone signs in.
  const { setLocale } = useUserPreferencesContext();

  function onSelectChange(e: React.ChangeEvent<HTMLSelectElement>) {
    const nextLocale = e.target.value;
    // Started before the navigation so the request is in flight regardless of when this
    // component unmounts. Saving it is what makes the AGENTS follow the switch: the API
    // trusts the stored preference over Accept-Language, which no client can guarantee.
    void setLocale(nextLocale);
    router.replace(pathname, { locale: nextLocale });
  }

  return (
    <div className={cn("relative", fullWidth && "w-full")}>
      <select
        value={locale}
        onChange={onSelectChange}
        aria-label={t("locale.switchLanguage")}
        className={cn(
          "appearance-none rounded-lg border border-white/10 bg-white/5 px-3 py-1.5 pr-8 text-sm text-gray-300 transition-colors hover:border-white/20 hover:bg-white/10 focus:border-primary-500/50 focus:outline-none focus:ring-1 focus:ring-primary-500/30",
          fullWidth && "w-full py-2.5",
        )}
      >
        {routing.locales.map((loc) => (
          <option key={loc} value={loc}>
            {localeFlags[loc]} {t(`locale.${loc}`)}
          </option>
        ))}
      </select>
      <svg
        className="pointer-events-none absolute right-2 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400"
        fill="none"
        viewBox="0 0 24 24"
        stroke="currentColor"
        strokeWidth={2}
      >
        <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
      </svg>
    </div>
  );
}
