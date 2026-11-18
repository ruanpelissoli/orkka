import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
// Imported off the module rather than the ui barrel: the barrel drags in the
// locale-aware navigation, which these assertions have no use for.
import {
  FloatingDockHost,
  FloatingDockSlot,
  FLOATING_DOCK_ORDER,
} from "@/components/ui/FloatingDock";

/**
 * The dock exists so two launchers (the run bubble and a page's agent chat pill) can never
 * be pinned to the same corner. These assert the contract that replaces the old hand-tuned
 * offsets: one container, one slot per launcher, ordered outwards from the corner.
 */
describe("FloatingDock", () => {
  it("stacks every launcher as a sibling inside the host, ordered from the corner", () => {
    render(
      <>
        <FloatingDockHost />
        <FloatingDockSlot order={FLOATING_DOCK_ORDER.run}>
          <button type="button">Run</button>
        </FloatingDockSlot>
        <FloatingDockSlot order={FLOATING_DOCK_ORDER.agentChat}>
          <button type="button">Chat</button>
        </FloatingDockSlot>
      </>
    );

    const run = screen.getByText("Run").parentElement;
    const chat = screen.getByText("Chat").parentElement;

    expect(run?.parentElement).not.toBeNull();
    expect(run?.parentElement).toBe(chat?.parentElement);
    // Column layout: neither launcher pins itself, so they cannot overlap.
    expect(run?.parentElement?.className).toContain("flex-col-reverse");
    // Chat is page-scoped, so it stacks above the shell-level run bubble.
    expect(Number(run?.style.order)).toBeLessThan(Number(chat?.style.order));
  });

  it("still renders a launcher when no host is mounted", () => {
    render(
      <FloatingDockSlot order={FLOATING_DOCK_ORDER.run}>
        <button type="button">Run</button>
      </FloatingDockSlot>
    );

    expect(screen.getByText("Run")).toBeInTheDocument();
  });
});
