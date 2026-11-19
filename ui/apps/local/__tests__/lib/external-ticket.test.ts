import { describe, it, expect } from "vitest";
import {
  NO_EPIC_FILTER,
  distinctEpics,
  matchesEpicFilter,
  stripTicketKey,
  trackerKindFromComment,
  trackerKindFromUrl,
} from "@/lib/external-ticket";

describe("external ticket helpers", () => {
  it.each([
    ["https://acme.atlassian.net/browse/SHOP-12", "jira"],
    ["https://dev.azure.com/acme/Shop/_workitems/edit/4567", "ado"],
    ["https://acme.visualstudio.com/Shop/_workitems/edit/4567", "ado"],
    ["https://github.com/acme/shop/issues/12", "github"],
    ["https://acme.ghe.com/acme/shop/issues/12", "github"],
    ["https://tracker.example.com/SHOP-12", null],
    ["not a url", null],
    [null, null],
  ] as const)("%s is %s", (url, kind) => {
    expect(trackerKindFromUrl(url)).toBe(kind);
  });

  it("strips the leading [KEY] only when it is the task's own key", () => {
    expect(stripTicketKey("[SHOP-12] Add invoice export", "SHOP-12")).toBe("Add invoice export");
    expect(stripTicketKey("[SHOP-12] Add invoice export", "SHOP-13")).toBe("[SHOP-12] Add invoice export");
    expect(stripTicketKey("[SHOP-12] Add invoice export", null)).toBe("[SHOP-12] Add invoice export");
    expect(stripTicketKey("Add invoice export [SHOP-12]", "SHOP-12")).toBe("Add invoice export [SHOP-12]");
    // A title that is only the key keeps it rather than going blank.
    expect(stripTicketKey("[SHOP-12]", "SHOP-12")).toBe("[SHOP-12]");
  });

  it("reads the tracker from an imported comment's header", () => {
    expect(trackerKindFromComment("**Ana** commented on SHOP-12 in Jira (2026-10-26 10:00 UTC):\n\nhi")).toBe("jira");
    expect(trackerKindFromComment("**Ana** commented on 4567 in Azure DevOps (2026-10-26 10:00 UTC):\n\nhi")).toBe("ado");
    expect(
      trackerKindFromComment("**ana** commented on acme/shop#12 in GitHub (2026-11-19 10:00 UTC):\n\nhi")
    ).toBe("github");
    expect(trackerKindFromComment("plain text")).toBeNull();
  });

  it("filters by epic, with a choice for tasks made on the board", () => {
    const fromTicket = { externalKey: "SHOP-12", externalEpic: "SHOP-1 Checkout" };
    const boardMade = { externalKey: null, externalEpic: null };
    expect(matchesEpicFilter(fromTicket, "")).toBe(true);
    expect(matchesEpicFilter(fromTicket, "SHOP-1 Checkout")).toBe(true);
    expect(matchesEpicFilter(fromTicket, "SHOP-2 Search")).toBe(false);
    expect(matchesEpicFilter(fromTicket, NO_EPIC_FILTER)).toBe(false);
    expect(matchesEpicFilter(boardMade, NO_EPIC_FILTER)).toBe(true);
    expect(distinctEpics([fromTicket, boardMade, { externalEpic: "SHOP-0 Auth" }, fromTicket])).toEqual([
      "SHOP-0 Auth",
      "SHOP-1 Checkout",
    ]);
  });
});
