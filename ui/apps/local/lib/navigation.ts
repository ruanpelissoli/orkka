import { useCallback } from "react";
import {
  useLocation,
  useNavigate,
  useParams as useRouteParams,
  useSearchParams as useRouteSearchParams,
} from "react-router";

/**
 * Route params and the query string, with the signatures the pages were written against
 * (Next's `next/navigation`), implemented on React Router. For links and navigation use the
 * locale-aware `@/i18n/navigation`.
 */

/** The current route's params, e.g. `{ locale, projectId, taskId }`. */
export function useParams<
  T extends Record<string, string | undefined> = Record<string, string>,
>(): T {
  return useRouteParams() as T;
}

/** The query string, read-only. Navigate with a new URL to change it. */
export function useSearchParams(): URLSearchParams {
  return useRouteSearchParams()[0];
}

/**
 * Changes the query string of the current page (`?task=`, `?file=`, `?d=`) as a new history
 * entry, without scrolling: the page stays mounted and re-renders with the new params.
 */
export function useUpdateSearchParams(): (update: (params: URLSearchParams) => void) => void {
  const navigate = useNavigate();
  const { search, hash } = useLocation();
  return useCallback(
    (update: (params: URLSearchParams) => void) => {
      const params = new URLSearchParams(search);
      update(params);
      const next = params.toString();
      void navigate({ search: next ? `?${next}` : "", hash }, { preventScrollReset: true });
    },
    [navigate, search, hash]
  );
}
