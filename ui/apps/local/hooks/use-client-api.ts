import { useCallback } from "react";

/**
 * Client-side access to the local host's API.
 *
 * Every API call runs in the browser, same origin. The browser authenticates with the host's
 * httpOnly session cookie, so the token is empty and the API client omits the Authorization
 * header. After a write that changes what a page shows, call `refreshData()`
 * (`@/lib/data-refresh`) so the page queries refetch.
 */
export function useClientApi() {
  return useCallback(async <T>(call: (token: string) => Promise<T>): Promise<T> => call(""), []);
}
