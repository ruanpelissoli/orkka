import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi, resetApiMocks } from "../mocks/api";
import { useLocale } from "use-intl";

import type { MemberPreferences } from "@/lib/types";
import { UserPreferencesProvider } from "@/components/providers/UserPreferencesProvider";
import { LocaleSwitcher } from "@/components/ui/LocaleSwitcher";

/**
 * The interface language is the only preference the BACKEND reads: the API prefers it over
 * Accept-Language when telling agents which language to answer in. That makes "did the switch
 * get persisted" a correctness question, not a nicety — an unsaved switch is a conversation
 * answered in the wrong language.
 */
describe("interface language preference", () => {
  const stored = (locale: string | null): MemberPreferences => ({
    showTechnicalDetails: false,
    onboardingTourSeen: true,
    locale,
  });

  beforeEach(() => {
    resetApiMocks();
    vi.mocked(useLocale).mockReturnValue("en");
  });

  function renderSwitcher(initial: MemberPreferences | null) {
    return render(
      <UserPreferencesProvider initial={initial} tokenFactory={async () => "token"}>
        <LocaleSwitcher />
      </UserPreferencesProvider>
    );
  }

  it("saves the language the member picks", async () => {
    renderSwitcher(stored(null));

    await userEvent.selectOptions(screen.getByRole("combobox"), "pt");

    await waitFor(() =>
      expect(mockApi.org.updateMyPreferences).toHaveBeenCalledWith(
        { locale: "pt" },
        "token"
      )
    );
  });

  it("adopts an interface language chosen before the preference existed", async () => {
    vi.mocked(useLocale).mockReturnValue("pt");

    renderSwitcher(stored(null));

    await waitFor(() =>
      expect(mockApi.org.updateMyPreferences).toHaveBeenCalledWith(
        { locale: "pt" },
        "token"
      )
    );
  });

  it("leaves an already-saved language alone", async () => {
    vi.mocked(useLocale).mockReturnValue("pt");

    renderSwitcher(stored("es"));

    await waitFor(() => expect(screen.getByRole("combobox")).toBeTruthy());
    expect(mockApi.org.updateMyPreferences).not.toHaveBeenCalled();
  });

  /** Browsing in the default language is not a choice — pinning "en" would override an
   *  Accept-Language the member may legitimately change later. */
  it("does not pin English on a member who never picked anything", async () => {
    renderSwitcher(stored(null));

    await waitFor(() => expect(screen.getByRole("combobox")).toBeTruthy());
    expect(mockApi.org.updateMyPreferences).not.toHaveBeenCalled();
  });

  /** Marketing pages render the switcher with no provider — there is no member to save for. */
  it("still switches the interface with no preferences provider mounted", async () => {
    render(<LocaleSwitcher />);

    await userEvent.selectOptions(screen.getByRole("combobox"), "fr");

    expect(mockApi.org.updateMyPreferences).not.toHaveBeenCalled();
  });
});
