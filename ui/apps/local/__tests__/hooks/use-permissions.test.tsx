import { describe, it, expect } from "vitest";
import { renderHook } from "@testing-library/react";
import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import { usePermissions } from "@/hooks/use-permissions";
import { ALL_PERMISSIONS } from "@/lib/constants";
import { ownerMe, memberMe } from "../render-with-permissions";
import type { OrgMe } from "@/lib/types";

function wrapperWith(me: OrgMe | null) {
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <PermissionsProvider me={me}>{children}</PermissionsProvider>;
  };
}

describe("usePermissions", () => {
  it("grants every permission implicitly to Owners", () => {
    const { result } = renderHook(() => usePermissions(), {
      wrapper: wrapperWith(ownerMe),
    });

    expect(result.current.me).toEqual(ownerMe);
    expect(result.current.isOwner).toBe(true);
    for (const permission of ALL_PERMISSIONS) {
      expect(result.current.has(permission)).toBe(true);
    }
  });

  it("grants Members only their explicitly granted permissions", () => {
    const me = memberMe(["TasksCreate", "PlansReview"]);
    const { result } = renderHook(() => usePermissions(), {
      wrapper: wrapperWith(me),
    });

    expect(result.current.isOwner).toBe(false);
    expect(result.current.has("TasksCreate")).toBe(true);
    expect(result.current.has("PlansReview")).toBe(true);
    expect(result.current.has("TasksDelete")).toBe(false);
    expect(result.current.has("BillingManage")).toBe(false);
  });

  it("denies everything with a null me (provider present, not loaded)", () => {
    const { result } = renderHook(() => usePermissions(), {
      wrapper: wrapperWith(null),
    });

    expect(result.current.me).toBeNull();
    expect(result.current.isOwner).toBe(false);
    for (const permission of ALL_PERMISSIONS) {
      expect(result.current.has(permission)).toBe(false);
    }
  });

  it("fails closed without a provider: has() is always false", () => {
    const { result } = renderHook(() => usePermissions());

    expect(result.current.me).toBeNull();
    expect(result.current.isOwner).toBe(false);
    for (const permission of ALL_PERMISSIONS) {
      expect(result.current.has(permission)).toBe(false);
    }
  });
});
