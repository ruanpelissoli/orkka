import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import "./mocks/navigation";
import "./mocks/intl";
import "./mocks/i18n-navigation";
import { renderWithPermissions, ownerMe, memberMe } from "./render-with-permissions";
import { getProjectNav, getGlobalNav, getMobileTabs } from "@/lib/nav";
import { MobileTabBar } from "@/components/ui";

const t = (key: string) => key;

describe("getProjectNav", () => {
  it("returns the full nav", () => {
    const nav = getProjectNav("p1", t);
    expect(nav.map((i) => i.key)).toEqual([
      "overview",
      "team",
      "activity",
      "skills",
      "automation",
      "memory",
      "roadmap",
      "board",
      "tasks",
      "repos",
      "documents",
      "design",
      "architect",
      "settings",
    ]);
  });

  it("leaves inbox to the global nav so it is not listed twice in the sidebar", () => {
    expect(getProjectNav("p1", t).some((i) => i.key === "inbox")).toBe(false);
    expect(getGlobalNav(t).some((i) => i.key === "inbox")).toBe(true);
  });

  // Local-only: one user, no team roster to link to.
  it("has no members page", () => {
    expect(getProjectNav("p1", t).some((i) => i.key === "members")).toBe(false);
  });

  it("marks only the overview link as exact", () => {
    const nav = getProjectNav("p1", t);
    expect(nav.filter((i) => i.exact).map((i) => i.key)).toEqual(["overview"]);
  });
});

describe("getGlobalNav", () => {
  it("leads with the workspace board, then inbox, notifications and account", () => {
    expect(getGlobalNav(t).map((i) => i.key)).toEqual(["workspaceBoard", "inbox", "notifications", "account"]);
  });

  it("links the workspace board to /board, active only on that page", () => {
    const board = getGlobalNav(t).find((i) => i.key === "workspaceBoard");
    expect(board).toMatchObject({ href: "/board", icon: "board", exact: true, label: "sidebar.workspaceBoard" });
  });
});

describe("getMobileTabs", () => {
  it("returns the 5-tab project set with a permission-gated create FAB", () => {
    const tabs = getMobileTabs("p1", t);
    expect(tabs.map((i) => i.key)).toEqual(["home", "board", "new", "alerts", "settings"]);
    const create = tabs.find((i) => i.key === "new");
    expect(create?.isCreate).toBe(true);
    expect(create?.permission).toBe("TasksCreate");
    expect(create?.href).toBe("/p/p1/tasks/new");
    expect(tabs.find((i) => i.key === "home")?.exact).toBe(true);
  });

  it("returns the 3-tab global set, led by the workspace board, without a create FAB", () => {
    const tabs = getMobileTabs(undefined, t);
    expect(tabs.map((i) => i.key)).toEqual(["board", "alerts", "settings"]);
    expect(tabs[0].href).toBe("/board");
    expect(tabs.some((i) => i.isCreate)).toBe(false);
  });
});

describe("MobileTabBar", () => {
  it("renders all tabs including the create FAB for an owner", () => {
    renderWithPermissions(<MobileTabBar tabs={getMobileTabs("p1", t)} />, ownerMe);
    expect(screen.getByText("mobileNav.board")).toBeInTheDocument();
    expect(screen.getByText("mobileNav.new")).toBeInTheDocument();
  });

  it("hides the create FAB for a member without TasksCreate", () => {
    renderWithPermissions(<MobileTabBar tabs={getMobileTabs("p1", t)} />, memberMe());
    expect(screen.getByText("mobileNav.board")).toBeInTheDocument();
    expect(screen.queryByText("mobileNav.new")).not.toBeInTheDocument();
  });

  it("shows the create FAB for a member with TasksCreate", () => {
    renderWithPermissions(<MobileTabBar tabs={getMobileTabs("p1", t)} />, memberMe(["TasksCreate"]));
    expect(screen.getByText("mobileNav.new")).toBeInTheDocument();
  });
});
