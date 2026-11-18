/**
 * Render helper that wraps UI in the PermissionsProvider so permission-gated
 * controls (RequirePermission / usePermissions) behave as they do in the real
 * app, where the dashboard layout provides the context from api.org.me.
 *
 * Without a provider the default context is fail-closed (has() === false),
 * which hides every gated control.
 */
import { render, type RenderResult } from "@testing-library/react";
import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import type { OrgMe } from "@/lib/types";
import type { Permission } from "@/lib/constants";

/** Tour marked seen: these fixtures are for permission-gated UI, and an unseen tour would
 *  arm the overlay in any test that happens to mount the dashboard shell. */
const testPreferences = { showTechnicalDetails: false, onboardingTourSeen: true };

/** The implicit local user: an Owner, so every permission gate is open. */
export const ownerMe: OrgMe = {
  orgId: "org1",
  orgName: "Acme",
  memberId: "m1",
  role: "Owner",
  permissions: [],
  preferences: testPreferences,
};

export function memberMe(permissions: Permission[] = []): OrgMe {
  return {
    orgId: "org1",
    orgName: "Acme",
    memberId: "m2",
    role: "Member",
    permissions,
    preferences: testPreferences,
  };
}

export function renderWithPermissions(
  ui: React.ReactElement,
  me: OrgMe | null = ownerMe
): RenderResult {
  return render(<PermissionsProvider me={me}>{ui}</PermissionsProvider>);
}
