import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import type { Notification } from "@/lib/types";

const mockUseNotifications = vi.fn();
vi.mock("@/hooks/use-notifications", () => ({
  useNotifications: (...args: unknown[]) => mockUseNotifications(...args),
}));

import NotificationsPage from "@/app/[locale]/(dashboard)/notifications/page";

const baseState = {
  notifications: [] as Notification[],
  unreadCount: 0,
  isLoading: false,
  markRead: vi.fn(),
  markAllRead: vi.fn(),
  refetch: vi.fn(),
};

function makeNotification(overrides: Partial<Notification> = {}): Notification {
  return {
    id: "01ARZ3NDEKTSV4RRFFQ69G5FAV",
    taskId: "01ARZ3NDEKTSV4RRFFQ69G5FA0",
    type: "PlanApprovalRequired",
    title: "Add login page",
    body: null,
    projectId: "01ARZ3NDEKTSV4RRFFQ69G5FA1",
    projectSlug: "acme-app",
    taskNumber: 7,
    agentId: null,
    conversationId: null,
    isRead: false,
    createdAt: new Date().toISOString(),
    ...overrides,
  };
}

describe("NotificationsPage", () => {
  it("renders the page title", () => {
    mockUseNotifications.mockReturnValue(baseState);
    render(<NotificationsPage />);
    expect(screen.getByText("notifications.title")).toBeInTheDocument();
  });

  it("renders the empty state when there are no notifications", () => {
    mockUseNotifications.mockReturnValue(baseState);
    render(<NotificationsPage />);
    expect(screen.getByText("notifications.empty")).toBeInTheDocument();
    expect(screen.getByText("notifications.emptyDescription")).toBeInTheDocument();
  });

  it("renders notification rows with localized type text and task title", () => {
    mockUseNotifications.mockReturnValue({
      ...baseState,
      notifications: [makeNotification()],
      unreadCount: 1,
    });
    render(<NotificationsPage />);
    expect(
      screen.getByText("notifications.types.PlanApprovalRequired")
    ).toBeInTheDocument();
    expect(screen.getByText(/Add login page/)).toBeInTheDocument();
  });

  it("renders RunnerOffline notifications (hybrid task parked)", () => {
    mockUseNotifications.mockReturnValue({
      ...baseState,
      notifications: [makeNotification({ type: "RunnerOffline" })],
      unreadCount: 1,
    });
    render(<NotificationsPage />);
    expect(
      screen.getByText("notifications.types.RunnerOffline")
    ).toBeInTheDocument();
  });

  it("shows mark-all-read only when there are unread notifications", () => {
    mockUseNotifications.mockReturnValue({
      ...baseState,
      notifications: [makeNotification()],
      unreadCount: 1,
    });
    render(<NotificationsPage />);
    expect(screen.getByText("notifications.markAllRead")).toBeInTheDocument();
  });

  it("hides mark-all-read when everything is read", () => {
    mockUseNotifications.mockReturnValue({
      ...baseState,
      notifications: [makeNotification({ isRead: true })],
      unreadCount: 0,
    });
    render(<NotificationsPage />);
    expect(screen.queryByText("notifications.markAllRead")).not.toBeInTheDocument();
  });
});
