// UI-locale resolution for API calls. The backend reads the
// Accept-Language header to localize AI agent responses.
import { routing } from "@/i18n/routing";

const SUPPORTED = routing.locales as readonly string[];

// React Native has a `window` but no `window.location`/`document`, so the browser
// detection below can never run there. The mobile app registers its own getter at
// startup (reading its persisted locale store) — mirrors configureStreamingFetch in
// sse.ts. Browsers never call this and keep the URL/cookie logic.
let clientLocaleGetter: (() => string) | null = null;
export function registerClientLocaleGetter(fn: () => string): void {
  clientLocaleGetter = fn;
}

// Browser: locale is the first URL segment (localePrefix "always"),
// falling back to the NEXT_LOCALE cookie, then the default.
export function getClientLocale(): string {
  if (clientLocaleGetter) return clientLocaleGetter();
  if (typeof window === "undefined" || !window.location || typeof document === "undefined") {
    return routing.defaultLocale;
  }
  const first = window.location.pathname.split("/")[1];
  if (SUPPORTED.includes(first)) return first;
  const cookie = document.cookie
    .split("; ")
    .find((c) => c.startsWith("NEXT_LOCALE="))
    ?.split("=")[1];
  return cookie && SUPPORTED.includes(cookie) ? cookie : routing.defaultLocale;
}

// The API client runs in the browser only, so the locale is always the client's. Async because
// the request headers are assembled asynchronously (see workspaceHeaders).
export async function resolveLocale(): Promise<string> {
  return getClientLocale();
}
