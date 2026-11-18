import { describe, it, expect } from "vitest";
import { render, screen, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import type { MyRunnerStatus } from "@/lib/types";

import { ForgeCliSection } from "@/components/runner/ForgeCliSection";

function status(overrides: Partial<MyRunnerStatus> = {}): MyRunnerStatus {
  return {
    online: true,
    machineName: "DESKTOP-RUAN",
    daemonVersion: "0.9.0",
    connectedAt: "2026-10-26T10:00:00Z",
    lastSeenAt: "2026-10-26T10:05:00Z",
    providers: [],
    lastKnownProviders: null,
    lastKnownAt: null,
    lastKnownMachineName: null,
    hasActiveTokens: true,
    ...overrides,
  };
}

/** The forge CLIs open the developer's pull requests, under their own logins. */
describe("ForgeCliSection", () => {
  it("shows each CLI as ready, not signed in, or not installed — with the accounts", () => {
    render(
      <ForgeCliSection
        status={status({
          forgeClis: [
            { cli: "gh", found: true, authenticated: true, accounts: ["ruan", "ruan-work"], detail: null },
            {
              cli: "az",
              found: true,
              authenticated: false,
              accounts: null,
              detail: "az is not signed in, or the azure-devops extension is missing.",
            },
            { cli: "glab", found: false, authenticated: false, accounts: null, detail: null },
          ],
        })}
      />
    );

    const gh = within(screen.getByTestId("forge-cli-gh"));
    expect(gh.getByText("state.ready")).toBeInTheDocument();
    expect(gh.getByText("signedInAs")).toBeInTheDocument();
    // A ready CLI needs no remedy.
    expect(gh.queryByText("gh auth login")).toBeNull();

    const az = within(screen.getByTestId("forge-cli-az"));
    expect(az.getByText("state.notSignedIn")).toBeInTheDocument();
    expect(az.getByText(/azure-devops extension is missing/)).toBeInTheDocument();
    // Both halves of az's remedy, each copyable.
    expect(az.getByText("az login")).toBeInTheDocument();
    expect(az.getByText("az extension add --name azure-devops")).toBeInTheDocument();

    const glab = within(screen.getByTestId("forge-cli-glab"));
    expect(glab.getByText("state.notInstalled")).toBeInTheDocument();
    expect(glab.getByText("glab auth login")).toBeInTheDocument();
  });

  it("treats a CLI the runner did not report as not installed", () => {
    render(
      <ForgeCliSection
        status={status({
          forgeClis: [{ cli: "gh", found: true, authenticated: true, accounts: ["ruan"], detail: null }],
        })}
      />
    );

    expect(within(screen.getByTestId("forge-cli-az")).getByText("state.notInstalled")).toBeInTheDocument();
  });

  it("says so when the runner is too old to report forge CLIs", () => {
    render(<ForgeCliSection status={status({ forgeClis: null })} />);

    expect(screen.getByText("notReported")).toBeInTheDocument();
    expect(screen.queryByTestId("forge-cli-gh")).toBeNull();
  });

  it("shows the never-connected state before any runner has connected", () => {
    render(
      <ForgeCliSection
        status={status({ online: false, machineName: null, lastSeenAt: null, forgeClis: null })}
      />
    );

    expect(screen.getByText("notConnected")).toBeInTheDocument();
  });

  it("marks an offline runner's report as last known", () => {
    render(
      <ForgeCliSection
        status={status({
          online: false,
          forgeClis: [{ cli: "gh", found: true, authenticated: true, accounts: ["ruan"], detail: null }],
        })}
      />
    );

    expect(screen.getByText("lastKnown")).toBeInTheDocument();
  });
});
