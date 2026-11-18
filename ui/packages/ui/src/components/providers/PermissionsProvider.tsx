import { createContext, useContext, useMemo } from "react";
import type { OrgMe } from "@/lib/types";
import type { Permission } from "@/lib/constants";

interface PermissionsContextValue {
  me: OrgMe | null;
  isOwner: boolean;
  /** Owners hold every permission implicitly, matching the API's PermissionGuard. */
  has: (permission: Permission) => boolean;
}

const PermissionsContext = createContext<PermissionsContextValue>({
  me: null,
  isOwner: false,
  has: () => false,
});

export function PermissionsProvider({
  me,
  children,
}: {
  me: OrgMe | null;
  children: React.ReactNode;
}) {
  const value = useMemo<PermissionsContextValue>(() => {
    const isOwner = me?.role === "Owner";
    const granted = new Set(me?.permissions ?? []);
    return {
      me,
      isOwner,
      has: (permission) => isOwner || granted.has(permission),
    };
  }, [me]);

  return (
    <PermissionsContext.Provider value={value}>
      {children}
    </PermissionsContext.Provider>
  );
}

export function usePermissionsContext() {
  return useContext(PermissionsContext);
}
