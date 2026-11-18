import { describe, it, expect, beforeEach } from "vitest";
import { screen, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { renderPage } from "../render-page";

import MachineSettingsPage from "@/app/[locale]/(dashboard)/settings/machine/page";

const offlineStatus = {
  online: false,
  machineName: null,
  daemonVersion: null,
  connectedAt: null,
  lastSeenAt: null,
  providers: null,
  hasActiveTokens: false,
};

describe("MachineSettingsPage", () => {
  beforeEach(() => {
    mockApi.runner.status.mockResolvedValue(offlineStatus);
    mockApi.runner.tokens.list.mockResolvedValue([]);
  });

  it("renders status and tokens sections, with no token creation (the launcher mints tokens)", async () => {
    await renderPage(<MachineSettingsPage />);
    // useTranslations("dashboard.runnerPage") echoes back bare keys
    expect(screen.getByText("tokens.title")).toBeInTheDocument();
    expect(screen.getByText("tokens.empty")).toBeInTheDocument();
    expect(screen.queryByText("tokens.generate")).not.toBeInTheDocument();
    expect(screen.queryByText("gettingStarted.title")).not.toBeInTheDocument();
  });

  it("explains how to approve repository commands, read-only", async () => {
    await renderPage(<MachineSettingsPage />);
    const card = screen.getByTestId("pending-commands");
    expect(card).toHaveTextContent("./orkka commands approve <id>");
    expect(within(card).queryByRole("button", { name: /approve/i })).toBeNull();
  });

  it("lists pending command approvals with the exact approve line for each", async () => {
    mockApi.runner.status.mockResolvedValue({
      ...offlineStatus,
      pendingCommandApprovals: [
        { id: "a1b2", repo: "acme/api", command: "npm run seed", kind: "Setup", machine: "DESKTOP" },
      ],
    });
    await renderPage(<MachineSettingsPage />);
    expect(screen.getByText("npm run seed")).toBeInTheDocument();
    expect(screen.getByText("./orkka commands approve a1b2")).toBeInTheDocument();
  });

  it("shows the never-connected empty state when no runner has connected", async () => {
    await renderPage(<MachineSettingsPage />);
    // RunnerStatusCard uses the dashboard.runnerPage.status namespace
    expect(screen.getByText("neverConnected")).toBeInTheDocument();
    expect(screen.getByText("offline")).toBeInTheDocument();
  });

  it("renders machine details and provider chips when a runner is online", async () => {
    mockApi.runner.status.mockResolvedValue({
      online: true,
      machineName: "DESKTOP-RUAN",
      daemonVersion: "0.4.0",
      connectedAt: "2026-07-13T10:00:00Z",
      lastSeenAt: "2026-07-13T10:05:00Z",
      providers: [
        {
          provider: "claude",
          cliFound: true,
          authenticated: true,
          models: null,
          detail: null,
        },
        {
          provider: "codex",
          cliFound: true,
          authenticated: false,
          models: null,
          detail: "Run codex login on the machine",
        },
      ],
      hasActiveTokens: true,
    });

    await renderPage(<MachineSettingsPage />);
    expect(screen.getByText("online")).toBeInTheDocument();
    expect(screen.getByText("DESKTOP-RUAN")).toBeInTheDocument();
    expect(screen.getByText("0.4.0")).toBeInTheDocument();
    // The readiness chip and the sign-in list both name each CLI.
    expect(screen.getAllByText("Claude").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Codex").length).toBeGreaterThan(0);
    // Provider detail rendered as subtext
    expect(
      screen.getByText("Run codex login on the machine")
    ).toBeInTheDocument();
  });

  it("lists each AI CLI with its version, sign-in mode, readiness and how to sign in — never a secret", async () => {
    mockApi.runner.status.mockResolvedValue({
      ...offlineStatus,
      online: true,
      machineName: "DESKTOP-RUAN",
      lastSeenAt: "2026-11-17T10:05:00Z",
      providers: [
        {
          provider: "claude",
          cliFound: true,
          authenticated: true,
          models: null,
          detail: null,
          authMode: "bedrock",
          authDetail: "us-east-1",
          cliVersion: "2.1.326",
        },
        {
          provider: "codex",
          cliFound: true,
          authenticated: true,
          models: null,
          detail: null,
          authMode: "login",
          authDetail: "ChatGPT",
          cliVersion: "0.163.0",
        },
        {
          provider: "copilot",
          cliFound: true,
          authenticated: false,
          models: null,
          detail: null,
          authMode: "none",
          authDetail: "acme.ghe.com",
          cliVersion: "1.0.120",
        },
        { provider: "gemini", cliFound: false, authenticated: false, models: null, detail: null },
      ],
    });

    await renderPage(<MachineSettingsPage />);

    const claude = screen.getByTestId("provider-sign-in-claude");
    expect(claude).toHaveTextContent("Claude — modes.bedrock (us-east-1)");
    expect(claude).toHaveTextContent("2.1.326");
    expect(claude).toHaveTextContent("ready");
    expect(claude).toHaveTextContent("./orkka workspace set-env <workspace> CLAUDE_CODE_USE_BEDROCK=1");

    const codex = screen.getByTestId("provider-sign-in-codex");
    expect(codex).toHaveTextContent("Codex — modes.chatgpt");
    expect(codex).not.toHaveTextContent("(ChatGPT)");
    expect(codex).toHaveTextContent("codex login");

    const copilot = screen.getByTestId("provider-sign-in-copilot");
    expect(copilot).toHaveTextContent("notReady");
    expect(copilot).toHaveTextContent("copilot");

    // A CLI that is not installed and cannot be guarded is not listed.
    expect(screen.queryByTestId("provider-sign-in-gemini")).toBeNull();
    expect(screen.getByTestId("provider-sign-in")).toHaveTextContent("never");
  });

  it("renders token rows with active and revoked states", async () => {
    mockApi.runner.tokens.list.mockResolvedValue([
      {
        id: "tok-1",
        name: "Work laptop",
        createdAt: "2026-06-01T00:00:00Z",
        lastUsedAt: "2026-07-13T09:00:00Z",
        lastMachineName: "DESKTOP-RUAN",
        revoked: false,
      },
      {
        id: "tok-2",
        name: "Old machine",
        createdAt: "2026-01-01T00:00:00Z",
        lastUsedAt: null,
        lastMachineName: null,
        revoked: true,
      },
    ]);

    await renderPage(<MachineSettingsPage />);
    expect(screen.getByText("Work laptop")).toBeInTheDocument();
    expect(screen.getByText("Old machine")).toBeInTheDocument();
    // TokenRow uses the dashboard.runnerPage.tokens namespace
    expect(screen.getByText("activeBadge")).toBeInTheDocument();
    expect(screen.getByText("revokedBadge")).toBeInTheDocument();
    expect(screen.getByText("neverUsed")).toBeInTheDocument();
  });
});
