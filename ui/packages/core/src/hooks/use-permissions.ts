import { usePermissionsContext } from "@/components/providers/PermissionsProvider";

/**
 * Current user's org permissions. UI gating only — the API enforces
 * authoritatively, so hiding a control is a courtesy, not a security boundary.
 */
export function usePermissions() {
  const { me, isOwner, has } = usePermissionsContext();
  return { me, isOwner, has };
}
