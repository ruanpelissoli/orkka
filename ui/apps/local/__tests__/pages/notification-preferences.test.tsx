import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";

import NotificationPrefsPage from "@/app/[locale]/(dashboard)/settings/notifications/page";

describe("NotificationPrefsPage", () => {
  it("renders heading and description", async () => {
    render(<NotificationPrefsPage />);
    expect(screen.getByText("notifications.settingsTitle")).toBeInTheDocument();
    expect(screen.getByText("notifications.settingsDescription")).toBeInTheDocument();
  });

  it("renders the desktop notifications toggle", async () => {
    render(<NotificationPrefsPage />);
    expect(screen.getByText("notifications.desktop.label")).toBeInTheDocument();
    expect(screen.getByRole("switch")).toBeInTheDocument();
  });
});
