import { useEffect, useLayoutEffect, useState, type ReactNode } from "react";
import { Navigate, useLocation } from "react-router";
import { IntlProvider, type AbstractIntlMessages } from "use-intl";
import { isLocale, preferredLocale, rememberLocale, type Locale } from "@/i18n/routing";
import { loadMessages } from "@/i18n/messages";
import { useParams } from "@/lib/navigation";
import { AppMotionConfig } from "@/components/ui";
import { setDefaultDocumentTitle } from "@/hooks/use-document-title";

/** The browser's own time zone: dates render in the user's local time. */
const TIME_ZONE = Intl.DateTimeFormat().resolvedOptions().timeZone;

/**
 * Everything under `/{locale}/…`. Loads that locale's catalogs, provides them to every page,
 * remembers the locale in the `NEXT_LOCALE` cookie and sets `<html lang>` and the default tab
 * title. A first segment that is not a locale means an unprefixed URL (`/dashboard`): it is
 * redirected to the preferred locale, the same rule the host applies server-side.
 */
export default function LocaleLayout({ children }: { children: ReactNode }) {
  const { locale } = useParams<{ locale: string }>();
  const location = useLocation();

  if (!isLocale(locale)) {
    const target = `/${preferredLocale()}${location.pathname === "/" ? "" : location.pathname}`;
    return <Navigate to={`${target}${location.search}${location.hash}`} replace />;
  }
  return <LocaleProvider locale={locale}>{children}</LocaleProvider>;
}

function LocaleProvider({ locale, children }: { locale: Locale; children: ReactNode }) {
  const [loaded, setLoaded] = useState<{ locale: Locale; messages: AbstractIntlMessages } | null>(
    null
  );

  useEffect(() => {
    let cancelled = false;
    void loadMessages(locale).then((messages) => {
      if (!cancelled) setLoaded({ locale, messages });
    });
    rememberLocale(locale);
    document.documentElement.lang = locale;
    return () => {
      cancelled = true;
    };
  }, [locale]);

  // The localized default title. A layout effect, so it lands before the page's own
  // useDocumentTitle (a passive effect) on first render.
  const appTitle = loaded ? appTitleOf(loaded.messages) : null;
  useLayoutEffect(() => {
    if (appTitle) setDefaultDocumentTitle(appTitle);
  }, [appTitle]);

  // Keep the previous locale's messages on screen while the next ones load (a locale switch),
  // and render nothing only on the very first load — it is a local file, so it is quick.
  if (!loaded) return null;

  return (
    <IntlProvider locale={loaded.locale} messages={loaded.messages} timeZone={TIME_ZONE}>
      <AppMotionConfig>{children}</AppMotionConfig>
    </IntlProvider>
  );
}

function appTitleOf(messages: AbstractIntlMessages): string | null {
  const common = messages.common;
  if (!common || typeof common !== "object") return null;
  const meta = (common as AbstractIntlMessages).meta;
  if (!meta || typeof meta !== "object") return null;
  const title = (meta as AbstractIntlMessages).appTitle;
  return typeof title === "string" ? title : null;
}
