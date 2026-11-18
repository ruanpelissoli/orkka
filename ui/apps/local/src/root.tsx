import { Navigate, Outlet, ScrollRestoration } from "react-router";
import { preferredLocale } from "@/i18n/routing";

/** Redirects `/` to the preferred locale (the host normally does this before the SPA loads). */
export function RootRedirect() {
  return <Navigate to={`/${preferredLocale()}`} replace />;
}

/** The app root: window scroll restoration on back/forward, top of page on a new navigation. */
export function Root() {
  return (
    <>
      <ScrollRestoration />
      <Outlet />
    </>
  );
}
