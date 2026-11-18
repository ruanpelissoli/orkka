import { usePermissions } from "@/hooks/use-permissions";
import type { Permission } from "@/lib/constants";

/**
 * Renders children only when the current user holds the permission (Owners
 * always do). Pass a fallback to render a disabled state instead of nothing.
 */
export function RequirePermission({
  permission,
  fallback = null,
  children,
}: {
  permission: Permission;
  fallback?: React.ReactNode;
  children: React.ReactNode;
}) {
  const { has } = usePermissions();
  return has(permission) ? <>{children}</> : <>{fallback}</>;
}
