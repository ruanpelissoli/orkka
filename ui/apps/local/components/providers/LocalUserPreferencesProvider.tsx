import { type ReactNode } from "react";
import { UserPreferencesProvider } from "@/components/providers";
import type { MemberPreferences } from "@/lib/types";

const emptyToken = async () => "";

/** Preferences for the local UI: seeded from `/api/org/me` (read by the dashboard layout) and
 * written back same-origin with the session cookie — the token is empty by design. */
export function LocalUserPreferencesProvider({
  initial,
  children,
}: {
  initial: MemberPreferences | null;
  children: ReactNode;
}) {
  return (
    <UserPreferencesProvider initial={initial} tokenFactory={emptyToken}>
      {children}
    </UserPreferencesProvider>
  );
}
