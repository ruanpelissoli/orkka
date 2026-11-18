import { vi } from "vitest";
import React from "react";

export const mockLinkClick = vi.fn();

/** The locale-aware router (`useRouter` from @/i18n/navigation). One stable object, like the real hook's. */
export const mockI18nRouter = {
  push: vi.fn(),
  replace: vi.fn(),
  back: vi.fn(),
  forward: vi.fn(),
};

vi.mock("@/i18n/navigation", () => ({
  Link: ({
    href,
    children,
    ...props
  }: {
    href: string;
    children: React.ReactNode;
    [key: string]: unknown;
  }) => React.createElement("a", { href, ...props }, children),
  usePathname: vi.fn(() => "/"),
  useRouter: vi.fn(() => mockI18nRouter),
  localizeHref: (href: string, locale: string) => (href.startsWith("/") ? `/${locale}${href}` : href),
  stripLocale: (pathname: string) => pathname,
}));
