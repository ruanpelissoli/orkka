import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent, within } from "@testing-library/react";
import { DndContext } from "@dnd-kit/core";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import type { ProjectTrackerConfig, TaskMessage, TaskSummary } from "@/lib/types";

import { TaskCard } from "@/components/board/TaskCard";
import { TicketSourceStatusLine } from "@/components/board/TicketSourceStatusLine";
import { MessageBubble } from "@/components/task/MessageBubble";

function summary(overrides: Partial<TaskSummary> = {}): TaskSummary {
  return {
    id: "task-1",
    number: 7,
    code: "SHOP-7",
    title: "[SHOP-12] Add invoice export",
    priority: "medium",
    type: "Feature",
    status: "Draft",
    currentLane: "Drafting",
    branchName: null,
    prUrl: null,
    queuePosition: null,
    tags: [],
    repoId: "repo-1",
    repoName: "api",
    repoColor: null,
    assigneeId: null,
    assigneeName: null,
    assigneeAvatarUrl: null,
    isBlocked: false,
    blockedBy: [],
    mergeFailureReason: null,
    createdAt: "2026-10-26T10:00:00Z",
    externalKey: "SHOP-12",
    externalUrl: "https://acme.atlassian.net/browse/SHOP-12",
    externalEpic: "SHOP-1 Checkout",
    ...overrides,
  } as TaskSummary;
}

function card(task: TaskSummary, onTaskClick = vi.fn()) {
  render(
    <DndContext>
      <TaskCard task={task} onTaskClick={onTaskClick} />
    </DndContext>
  );
  return onTaskClick;
}

describe("Board card ticket badge", () => {
  it("shows the key as a Jira badge and drops it from the title", () => {
    card(summary());

    const badge = screen.getByTestId("external-ticket-badge");
    expect(badge).toHaveTextContent("SHOP-12");
    expect(badge.querySelector('[data-tracker-icon="jira"]')).not.toBeNull();
    expect(screen.getByText("Add invoice export")).toBeInTheDocument();
    expect(screen.queryByText(/\[SHOP-12\]/)).toBeNull();
  });

  it("links to the ticket in a new tab without opening the task", () => {
    const onTaskClick = card(summary());

    const badge = screen.getByTestId("external-ticket-badge");
    expect(badge).toHaveAttribute("href", "https://acme.atlassian.net/browse/SHOP-12");
    expect(badge).toHaveAttribute("target", "_blank");
    fireEvent.click(badge);
    expect(onTaskClick).not.toHaveBeenCalled();
  });

  it("uses the Azure DevOps icon for an ADO ticket", () => {
    card(
      summary({
        title: "[4567] Fix login",
        externalKey: "4567",
        externalUrl: "https://dev.azure.com/acme/Shop/_workitems/edit/4567",
      })
    );

    expect(
      screen.getByTestId("external-ticket-badge").querySelector('[data-tracker-icon="ado"]')
    ).not.toBeNull();
    expect(screen.getByText("Fix login")).toBeInTheDocument();
  });

  it("leaves a board-made task's title alone and shows no badge", () => {
    card(summary({ title: "[WIP] Refactor", externalKey: null, externalUrl: null, externalEpic: null }));

    expect(screen.queryByTestId("external-ticket-badge")).toBeNull();
    expect(screen.getByText("[WIP] Refactor")).toBeInTheDocument();
  });
});

function message(overrides: Partial<TaskMessage>): TaskMessage {
  return {
    id: "m1",
    taskId: "task-1",
    role: "tracker",
    content: "**Ana** commented on SHOP-12 in Jira (2026-10-26 10:00 UTC):\n\nPlease also export **CSV**.",
    messageType: "external_comment",
    metadata: {},
    createdAt: "2026-10-26T10:00:00Z",
    ...overrides,
  };
}

describe("ExternalComment message", () => {
  it("renders as a tracker comment with its source and markdown body", () => {
    render(<MessageBubble message={message({ messageType: "ExternalComment" as TaskMessage["messageType"] })} />);

    const bubble = screen.getByTestId("external-comment");
    expect(within(bubble).getByText("fromTracker")).toBeInTheDocument();
    expect(bubble.querySelector('[data-tracker-icon="jira"]')).not.toBeNull();
    // Markdown, like any other message: the bold renders as <strong>.
    expect(within(bubble).getByText("CSV").tagName).toBe("STRONG");
  });

  it("shows the Azure DevOps icon for an ADO comment", () => {
    render(
      <MessageBubble
        message={message({
          content: "**Ana** commented on 4567 in Azure DevOps (2026-10-26 10:00 UTC):\n\nok",
        })}
      />
    );

    expect(
      screen.getByTestId("external-comment").querySelector('[data-tracker-icon="ado"]')
    ).not.toBeNull();
  });

  it("keeps ingestion's system notes as system messages", () => {
    render(
      <MessageBubble
        message={message({
          role: "system",
          messageType: "system_event",
          content: "Imported from Jira but not submitted: no repository.",
        })}
      />
    );

    expect(screen.queryByTestId("external-comment")).toBeNull();
    expect(screen.getByText("Imported from Jira but not submitted: no repository.")).toBeInTheDocument();
  });
});

function trackerConfig(overrides: Partial<ProjectTrackerConfig> = {}): ProjectTrackerConfig {
  return {
    id: "cfg",
    projectId: "proj-1",
    ownerUserId: "local_user",
    trackerConnectionId: "conn",
    connection: {
      id: "conn",
      key: "jira-acme",
      kind: "jira",
      siteUrl: "https://acme.atlassian.net",
      accountId: null,
      displayName: null,
      healthy: true,
      healthDetail: null,
      machineName: "dana-laptop",
      lastSeenAt: "2026-10-26T10:00:00Z",
      checkedAt: null,
    },
    scope: "SHOP",
    extraClause: null,
    includeDescendants: false,
    autoSubmit: true,
    pollIntervalSeconds: 90,
    statusMap: {},
    branchTemplate: "{type}/{key}-{slug}",
    enabled: true,
    lastPolledAt: new Date().toISOString(),
    lastPollError: null,
    epics: [],
    scopeMode: "All",
    intakeStatuses: ["To Do"],
    routingRules: [],
    defaultRepoId: null,
    storyMode: 1,
    ...overrides,
  };
}

describe("Ticket source status line", () => {
  it("says where tickets come from and when they were last checked", () => {
    render(<TicketSourceStatusLine config={trackerConfig()} />);

    const line = screen.getByTestId("ticket-source-status");
    expect(line).toHaveTextContent("checked");
    expect(line.className).not.toContain("amber");
  });

  it("shows the last poll error as a warning", () => {
    render(<TicketSourceStatusLine config={trackerConfig({ lastPollError: "JQL syntax error" })} />);

    const line = screen.getByTestId("ticket-source-status");
    expect(line).toHaveTextContent("error");
    expect(line.className).toContain("amber");
  });

  it("asks for the runner when nothing has been polled yet", () => {
    render(<TicketSourceStatusLine config={trackerConfig({ lastPolledAt: null })} />);

    expect(screen.getByTestId("ticket-source-status")).toHaveTextContent("notPolled");
  });

  it("renders nothing without a ticket source", () => {
    const { container } = render(<TicketSourceStatusLine config={null} />);

    expect(container).toBeEmptyDOMElement();
  });
});
