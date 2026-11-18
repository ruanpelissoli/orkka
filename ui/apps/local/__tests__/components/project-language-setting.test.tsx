/**
 * The project language setting is what agents write in — decoupled from the navbar UI
 * locale. Pinned here: the select shows the stored language, falls back to the current UI
 * locale for projects that predate the setting (the API resolves it the same way), and a
 * change PATCHes the FULL settings bag with only `language` overridden — the settings
 * payload is a whole-bag replacement, so dropping the spread would wipe other settings.
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";

vi.mock("@/hooks/use-client-api", () => ({
  useClientApi:
    () =>
    <T,>(fn: (token: string) => Promise<T>) =>
      fn("test-token"),
}));

import { ProjectLanguageSetting } from "@/components/project/ProjectLanguageSetting";
import type { Project } from "@/lib/types";

function project(language: string | null): Project {
  return {
    id: "proj-1",
    name: "Tapago",
    settings: {
      maxParallelTasks: 2,
      sameRepoParallelism: true,
      autoApproveCodeReview: true,
      autoPushOnMerge: false,
      qaEnabled: false,
      manualVerificationEnabled: false,
      showAgentToolCalls: false,
      documentationSyncEnabled: false,
      language,
    },
  } as Project;
}

describe("ProjectLanguageSetting", () => {
  beforeEach(() => {
    mockApi.projects.update.mockClear();
    mockApi.projects.update.mockResolvedValue({});
  });

  it("shows the stored project language", () => {
    renderWithPermissions(<ProjectLanguageSetting project={project("pt")} />);
    expect(screen.getByRole("combobox")).toHaveValue("pt");
  });

  it("says Automatic — not English — when the project has no language set", () => {
    // The picker used to show the UI locale here, which read as a decision nobody had
    // made: it said English while the agent, having been told nothing, answered in
    // Portuguese. An unset language is its own state and the select has to show it.
    renderWithPermissions(<ProjectLanguageSetting project={project(null)} />);

    expect(screen.getByRole("combobox")).toHaveValue("");
  });

  it("saves the full settings bag with the new language", async () => {
    renderWithPermissions(<ProjectLanguageSetting project={project("en")} />);

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "pt" } });

    await waitFor(() => expect(mockApi.projects.update).toHaveBeenCalledTimes(1));
    const [id, payload, token] = mockApi.projects.update.mock.calls[0];
    expect(id).toBe("proj-1");
    expect(token).toBe("test-token");
    // Full replacement: every prior setting must survive the language change.
    expect(payload.settings).toMatchObject({
      maxParallelTasks: 2,
      sameRepoParallelism: true,
      autoApproveCodeReview: true,
      language: "pt",
    });
  });

  it("reverts optimistic state when the save fails", async () => {
    mockApi.projects.update.mockRejectedValueOnce(new Error("boom"));
    renderWithPermissions(<ProjectLanguageSetting project={project("en")} />);

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "de" } });

    await waitFor(() =>
      expect(
        screen.getByText("settingsPage.language.saveError")
      ).toBeInTheDocument()
    );
    expect(screen.getByRole("combobox")).toHaveValue("en");
  });
});
