import React from "react";
import { describe, it, expect, vi } from "vitest";
import { render, fireEvent } from "@testing-library/react";
import "../mocks/intl";

// framer-motion needs real DOM animation APIs; collapse motion.* to plain elements.
vi.mock("framer-motion", async (importOriginal) => {
  const actual = await importOriginal<typeof import("framer-motion")>();
  return {
    ...actual,
    motion: new Proxy({} as Record<string, unknown>, {
      get: (_t, tag: string) => {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const Plain = React.forwardRef((props: any, ref) => {
          const { children, initial, animate, exit, transition, ...rest } = props;
          void initial; void animate; void exit; void transition;
          return React.createElement(tag, { ...rest, ref }, children);
        });
        Plain.displayName = `motion.${tag}`;
        return Plain;
      },
    }),
    AnimatePresence: ({ children }: { children: React.ReactNode }) =>
      React.createElement(React.Fragment, null, children),
  };
});

import { Term } from "@/components/ui/Term";

// The intl mock's t() echoes the key, so the definition surfaces as
// "repo.definition" — enough to assert the popover opens and closes.
describe("Term", () => {
  it("renders its children with a hover affordance, no popover initially", () => {
    const { getByText, queryByRole } = render(<Term k="repo">repos</Term>);
    expect(getByText("repos")).toBeTruthy();
    expect(queryByRole("tooltip")).toBeNull();
  });

  it("opens the definition on focus and closes on Escape", () => {
    const { getByText, queryByRole } = render(<Term k="repo">repos</Term>);
    const trigger = getByText("repos");
    fireEvent.focus(trigger);
    expect(queryByRole("tooltip")?.textContent).toContain("repo.definition");
    fireEvent.keyDown(trigger, { key: "Escape" });
    expect(queryByRole("tooltip")).toBeNull();
  });

  it("toggles on click for touch users", () => {
    const { getByText, queryByRole } = render(<Term k="merge">merge</Term>);
    const trigger = getByText("merge");
    fireEvent.click(trigger);
    expect(queryByRole("tooltip")).toBeTruthy();
    fireEvent.click(trigger);
    expect(queryByRole("tooltip")).toBeNull();
  });

  it("falls back to the glossary term when no children are given", () => {
    const { getByText } = render(<Term k="pr" />);
    expect(getByText("pr.term")).toBeTruthy();
  });

  it("renders plain text when plain is set", () => {
    const { getByText, queryByRole } = render(
      <Term k="repo" plain>
        repos
      </Term>
    );
    const label = getByText("repos");
    expect(label.getAttribute?.("data-glossary-term")).toBeFalsy();
    fireEvent.focus(label);
    expect(queryByRole("tooltip")).toBeNull();
  });
});
