import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/i18n-navigation";
import type { TaskMessage } from "@/lib/types";

// Unlike the shared intl mock, echo the interpolated values too, so the dollar figures
// the card passes to its copy can be asserted.
vi.mock("use-intl", () => {
  const t = (key: string, values?: Record<string, unknown>) =>
    values ? `${key} ${JSON.stringify(values)}` : key;
  t.rich = t;
  t.markup = t;
  t.raw = t;
  t.has = () => true;
  return {
    useTranslations: () => t,
    useLocale: () => "en",
    useFormatter: () => ({ dateTime: (d: Date) => d.toISOString(), relativeTime: () => "just now" }),
    useNow: () => new Date(),
  };
});

import { MessageBubble } from "@/components/task/MessageBubble";

function checkpointMessage(spendCheckpoint: Record<string, unknown>): TaskMessage {
  return {
    id: "m1",
    taskId: "task-1",
    role: "assistant",
    content: "Spend limit reached.",
    messageType: "question",
    metadata: { spendCheckpoint },
    createdAt: "2026-11-17T10:00:00Z",
  };
}

/**
 * The spend checkpoint card reads the API's US-dollar metadata (spentUsd / limitUsd /
 * allotmentUsd) as-is — the estimated list-price spend, with no thousandths conversion.
 */
describe("Spend checkpoint card", () => {
  it("shows the dollar figures straight from the metadata", () => {
    render(
      <MessageBubble
        message={checkpointMessage({ spentUsd: 5.25, limitUsd: 5, allotmentUsd: 5, phase: "Developing" })}
      />
    );

    expect(screen.getByText(/^spendCheckpointBody /).textContent).toContain(
      '"spent":"≈ $5.25","limit":"$5.00"'
    );
    expect(screen.getByText(/^spendCheckpointHint /).textContent).toContain('"allotment":"$5.00"');
  });

  it("falls back to the per-run copy and no allotment when the limits are null", () => {
    render(
      <MessageBubble
        message={checkpointMessage({ spentUsd: 12, limitUsd: null, allotmentUsd: null, phase: "Planning" })}
      />
    );

    expect(screen.getByText(/^spendCheckpointBodyPlatform /).textContent).toContain('"spent":"≈ $12.00"');
    expect(screen.getByText("spendCheckpointHintNoAllotment")).toBeInTheDocument();
  });

  it("ignores the retired credits fields", () => {
    render(
      <MessageBubble
        message={checkpointMessage({ spentCredits: 5000, limitCredits: 5000, allotmentCredits: 5000 })}
      />
    );

    // Nothing in USD means nothing spent and no limit — never 5000 read as dollars.
    expect(screen.getByText(/^spendCheckpointBodyPlatform /).textContent).toContain('"spent":"≈ $0.00"');
  });
});
