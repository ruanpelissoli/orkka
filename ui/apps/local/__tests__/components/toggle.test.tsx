import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { Toggle } from "@/components/ui/Toggle";

describe("Toggle", () => {
  // Beside a long hint in a flex row (Ticket source → "Start work automatically") the switch
  // was squeezed narrower than its 44px track and its knob stuck out past the edge.
  it("never shrinks in a flex row", () => {
    render(
      <div style={{ display: "flex", width: 120 }}>
        <p>{"A very long explanation ".repeat(20)}</p>
        <Toggle checked onChange={() => {}} />
      </div>,
    );
    const toggle = screen.getByRole("switch");
    expect(toggle.className).toContain("shrink-0");
    expect(toggle.className).toContain("w-11");
  });
});
