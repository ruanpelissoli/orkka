import { useMemo, type AnchorHTMLAttributes, type Ref } from "react";
import { Link as RouterLink, useLocation, useNavigate } from "react-router";
import { useLocale } from "use-intl";
import { routing } from "./routing";

/**
 * Locale-aware navigation on React Router, with the API the app was written against
 * (next-intl's `createNavigation`): hrefs are written WITHOUT the locale (`/p/1/board`) and
 * every link and navigation adds the current one (`/en/p/1/board`).
 */

/** Adds the locale prefix to an app-absolute href; anything else (relative, #, http…) is left alone. */
export function localizeHref(href: string, locale: string): string {
  if (!href.startsWith("/") || href.startsWith("//")) return href;
  if (href === "/") return `/${locale}`;
  if (href.startsWith("/?") || href.startsWith("/#")) return `/${locale}${href.slice(1)}`;
  return `/${locale}${href}`;
}

/** Removes the locale prefix: `/en/p/1` → `/p/1`, `/en` → `/`. */
export function stripLocale(pathname: string): string {
  const [, first, ...rest] = pathname.split("/");
  if (!(routing.locales as readonly string[]).includes(first ?? "")) return pathname;
  const path = rest.join("/");
  return path === "" ? "/" : `/${path}`;
}

export interface LinkProps extends Omit<AnchorHTMLAttributes<HTMLAnchorElement>, "href"> {
  href: string;
  /** Link to the same page in another locale. */
  locale?: string;
  replace?: boolean;
  /** `false` keeps the scroll position (React Router's preventScrollReset). */
  scroll?: boolean;
  ref?: Ref<HTMLAnchorElement>;
}

export function Link({ href, locale, replace, scroll, ref, ...props }: LinkProps) {
  const current = useLocale();
  const target = localizeHref(href, locale ?? current);
  // React Router handles in-app paths; a full URL is an ordinary anchor.
  if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith("//")) {
    return <a {...props} href={target} ref={ref} />;
  }
  return (
    <RouterLink
      {...props}
      to={target}
      replace={replace}
      preventScrollReset={scroll === false}
      ref={ref}
    />
  );
}

export interface NavigateOptions {
  locale?: string;
  scroll?: boolean;
}

export interface AppRouter {
  push(href: string, options?: NavigateOptions): void;
  replace(href: string, options?: NavigateOptions): void;
  back(): void;
  forward(): void;
}

/** A stable router object for the current locale. */
export function useRouter(): AppRouter {
  const navigate = useNavigate();
  const locale = useLocale();
  return useMemo(() => {
    const go = (href: string, replace: boolean, options?: NavigateOptions) =>
      void navigate(localizeHref(href, options?.locale ?? locale), {
        replace,
        preventScrollReset: options?.scroll === false,
      });
    return {
      push: (href, options) => go(href, false, options),
      replace: (href, options) => go(href, true, options),
      back: () => void navigate(-1),
      forward: () => void navigate(1),
    };
  }, [navigate, locale]);
}

/** The current path without its locale prefix (`/p/1/board`). */
export function usePathname(): string {
  return stripLocale(useLocation().pathname);
}
