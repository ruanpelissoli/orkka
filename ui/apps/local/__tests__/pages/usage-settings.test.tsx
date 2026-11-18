import { describe, it, expect, beforeEach } from "vitest";
import { screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage as renderClientPage } from "../render-page";

import UsageSettingsPage from "@/app/[locale]/(dashboard)/settings/usage/page";

const zeroSummary = {
  totalCostUsd: 0,
  totalInputTokens: 1_000,
  totalOutputTokens: 500,
  totalCacheReadTokens: 40_000,
  totalCacheWriteTokens: 2_000,
  totalTokens: 43_500,
  taskCount: 3,
};

function row(groupKey: string, groupName: string, costUsd: number, totalTokens: number) {
  return {
    groupKey,
    groupName,
    costUsd,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    totalTokens,
    taskCount: 1,
  };
}

async function renderPage(sp: { days?: string; groupBy?: string } = {}) {
  setRoute({}, new URLSearchParams(sp).toString());
  await renderClientPage(<UsageSettingsPage />);
}

describe("Settings → Usage", () => {
  beforeEach(() => {
    mockApi.usage.summary.mockResolvedValue(zeroSummary);
    mockApi.usage.daily.mockResolvedValue({
      items: [{ date: "2026-11-16", costUsd: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, totalTokens: 43_500, taskCount: 3 }],
    });
    mockApi.usage.breakdown.mockResolvedValue({ items: [row("p1", "Acme API", 0, 43_500)] });
  });

  it("shows tokens and hides cost columns when there is no list-price estimate", async () => {
    await renderPage();
    expect(screen.getAllByText("43.5K").length).toBeGreaterThan(0);
    expect(screen.getByText("Acme API")).toBeInTheDocument();
    expect(screen.getByText("noCostHint")).toBeInTheDocument();
    expect(document.body.textContent).not.toContain("$");
    expect(mockApi.usage.breakdown).toHaveBeenCalledWith("project", expect.any(String), expect.any(String), expect.anything());
  });

  it("labels a non-zero cost as a list-price estimate", async () => {
    mockApi.usage.summary.mockResolvedValue({ ...zeroSummary, totalCostUsd: 0.42 });
    mockApi.usage.breakdown.mockResolvedValue({ items: [row("m1", "claude-sonnet", 0.42, 43_500)] });
    await renderPage({ groupBy: "model", days: "7" });
    expect(screen.getAllByText("≈ $0.42").length).toBeGreaterThan(0);
    expect(screen.getByText("listPriceHint")).toBeInTheDocument();
    expect(mockApi.usage.breakdown).toHaveBeenCalledWith("model", expect.any(String), expect.any(String), expect.anything());
  });

  it("falls back to the project grouping for an unknown groupBy", async () => {
    await renderPage({ groupBy: "member" });
    expect(mockApi.usage.breakdown).toHaveBeenLastCalledWith("project", expect.any(String), expect.any(String), expect.anything());
  });
});
