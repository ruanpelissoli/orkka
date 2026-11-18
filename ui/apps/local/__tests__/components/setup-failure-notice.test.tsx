import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";

import { SetupFailureNotice } from "@/components/task/SetupFailureNotice";

describe("SetupFailureNotice", () => {
  it("shows the reason the workspace won't run", () => {
    render(<SetupFailureNotice reason="Setup command failed (exit 1)." />);

    expect(screen.getByText("setupFailed")).toBeInTheDocument();
    expect(screen.getByText("Setup command failed (exit 1).")).toBeInTheDocument();
    // Says what to do about it, not just that it broke.
    expect(screen.getByText("setupFailedHint")).toBeInTheDocument();
  });

  it("renders nothing when setup succeeded", () => {
    const { container } = render(<SetupFailureNotice reason={null} />);

    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing for an older API that doesn't send the field", () => {
    const { container } = render(<SetupFailureNotice reason={undefined} />);

    expect(container).toBeEmptyDOMElement();
  });
});
