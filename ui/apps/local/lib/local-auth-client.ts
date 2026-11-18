/**
 * Browser-side identity for the local UI. There is no account: the browser carries the
 * host's httpOnly `orkka_s` session cookie on same-origin requests, so the "token" handed to
 * the shared API client is empty and `doFetch` sends no Authorization header (see
 * packages/core lib/api.ts). Same shape as the hook it replaced, so components keep their
 * `const { getToken } = useAuth()` call sites.
 */
export const LOCAL_USER_ID = "local_user";

const getToken = async (): Promise<string> => "";

const localAuth = { userId: LOCAL_USER_ID, isSignedIn: true, getToken } as const;

export function useAuth() {
  return localAuth;
}
