/**
 * The UI's locales. Every URL carries one as its first segment (`/en/dashboard`) — there is no
 * unprefixed URL: the host redirects `/` and unprefixed paths to `/{locale}/…`, and the SPA does
 * the same client-side (see `preferredLocale` and app/[locale]/layout.tsx).
 */
export const routing = {
  locales: ["en", "es", "pt", "fr", "de"],
  defaultLocale: "en",
} as const;

export type Locale = (typeof routing.locales)[number];

/**
 * The cookie that remembers the last locale the user browsed in. The name is next-intl's, kept
 * so existing browsers keep their choice; the host's locale redirect and `getClientLocale`
 * (packages/core lib/locale) read it.
 */
export const LOCALE_COOKIE = "NEXT_LOCALE";

export function isLocale(value: string | null | undefined): value is Locale {
  return !!value && (routing.locales as readonly string[]).includes(value);
}

function cookieLocale(): Locale | null {
  if (typeof document === "undefined") return null;
  const value = document.cookie
    .split("; ")
    .find((c) => c.startsWith(`${LOCALE_COOKIE}=`))
    ?.slice(LOCALE_COOKIE.length + 1);
  return isLocale(value) ? value : null;
}

/**
 * The locale for a URL that has none: the locale cookie, then the browser's languages (by
 * primary subtag, so `pt-BR` → `pt`), then the default. The host applies the same order with
 * the Accept-Language header.
 */
export function preferredLocale(): Locale {
  const fromCookie = cookieLocale();
  if (fromCookie) return fromCookie;
  const languages = typeof navigator === "undefined" ? [] : (navigator.languages ?? []);
  for (const tag of languages) {
    const primary = tag.toLowerCase().split("-")[0];
    if (isLocale(primary)) return primary;
  }
  return routing.defaultLocale;
}

/** Remembers the locale for a year, the way next-intl's middleware did. */
export function rememberLocale(locale: Locale): void {
  document.cookie = `${LOCALE_COOKIE}=${locale}; path=/; max-age=31536000; samesite=lax`;
}
