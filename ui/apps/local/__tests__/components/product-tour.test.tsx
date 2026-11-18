import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { render } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi, resetApiMocks } from "../mocks/api";

import type { Project, MemberPreferences } from "@/lib/types";
import { getProjectNav, getGlobalNav } from "@/lib/nav-model";
import { getTourSteps, placeCard } from "@/components/tour";
import { ProductTour, STEP_STORAGE_KEY } from "@/components/tour";
import {
  UserPreferencesProvider,
  useUserPreferencesContext,
} from "@/components/providers/UserPreferencesProvider";

const acmeProject = {
  id: "proj-1",
  slug: "acme",
  name: "Acme",
} as unknown as Project;

const projectCtx = { currentProject: acmeProject as Project | null, projects: [acmeProject] };
vi.mock("@/hooks/use-project", () => ({
  useProject: () => projectCtx,
}));

function renderTour(preferences: MemberPreferences) {
  return render(
    <UserPreferencesProvider initial={preferences} tokenFactory={async () => "token"}>
      <ProductTour />
    </UserPreferencesProvider>
  );
}

/** Stands in for the Replay control on the Preferences page, which re-arms the tour by
 *  clearing the member flag on the live provider — not by remounting it. */
function ReplayTrigger() {
  const { setOnboardingTourSeen } = useUserPreferencesContext();
  return <button onClick={() => void setOnboardingTourSeen(false)}>replay</button>;
}

const unseen: MemberPreferences = {
  showTechnicalDetails: false,
  onboardingTourSeen: false,
};

/**
 * The tour is a one-time introduction that navigates the user through every page. Two things
 * make or break it: that it covers the nav it claims to cover, and that skipping it is final.
 */
describe("product tour", () => {
  beforeEach(() => {
    resetApiMocks();
    projectCtx.currentProject = acmeProject;
    window.sessionStorage.clear();
  });

  describe("step coverage", () => {
    // The tour promises a walk through "every page". A page added to the nav without a step
    // here silently drops out of the tour, which is invisible until a user notices the gap.
    it("has a step for every sidebar destination", () => {
      const t = (key: string) => key;
      const navKeys = [
        ...getProjectNav("acme", t),
        ...getGlobalNav(t),
      ].map((item) => item.key);

      const stepKeys = new Set(getTourSteps("acme").map((step) => step.key));

      expect(navKeys.filter((key) => !stepKeys.has(key))).toEqual([]);
    });

    it("points each step at the route its nav item links to", () => {
      const t = (key: string) => key;
      const hrefByKey = new Map(
        [...getProjectNav("acme", t), ...getGlobalNav(t)].map((item) => [
          item.key,
          item.href,
        ])
      );

      for (const step of getTourSteps("acme")) {
        if (!hrefByKey.has(step.key)) continue;
        expect(step.href).toBe(hrefByKey.get(step.key));
      }
    });

    it("opens on a welcome card that belongs to no page", () => {
      const [first] = getTourSteps("acme");
      // Navigating before the user has agreed to a tour would yank them off the page they
      // just landed on.
      expect(first.key).toBe("welcome");
      expect(first.href).toBeUndefined();
    });
  });

  describe("card placement", () => {
    const card = { width: 340, height: 200 };
    const desktop = { width: 1440, height: 900 };
    const anchor = { top: 300, left: 12, width: 216, height: 44 };

    it("sits to the right of the anchor when there is room", () => {
      const placement = placeCard(anchor, card, desktop);
      expect(placement.arrow).toBe("left");
      expect(placement.left).toBe(anchor.left + anchor.width + 16);
    });

    it("drops below the anchor when the right edge would overflow", () => {
      const placement = placeCard({ ...anchor, left: 1200 }, card, desktop);
      expect(placement.arrow).toBe("top");
      expect(placement.top).toBe(anchor.top + anchor.height + 16);
      expect(placement.left + card.width).toBeLessThanOrEqual(desktop.width);
    });

    it("centers when there is no anchor at all", () => {
      // The mobile case: the sidebar lives in a closed drawer, so nothing can be spotlighted.
      const placement = placeCard(null, card, { width: 390, height: 780 });
      expect(placement.arrow).toBeNull();
      expect(placement.left).toBeGreaterThanOrEqual(16);
    });

    it("keeps the card on screen when it is taller than the viewport", () => {
      const placement = placeCard(null, { width: 340, height: 900 }, { width: 390, height: 500 });
      expect(placement.top).toBe(16);
    });
  });

  describe("lifecycle", () => {
    it("starts on a Ready project when the member has not seen it", async () => {
      renderTour(unseen);
      expect(await screen.findByRole("dialog")).toBeInTheDocument();
      expect(screen.getByText("tour.steps.welcome.title")).toBeInTheDocument();
    });

    it("stays away once the member has seen it", () => {
      renderTour({ showTechnicalDetails: false, onboardingTourSeen: true });
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });

    it("waits for a project to be open", () => {
      // Every project step links into a project; with none open there is nothing to tour.
      projectCtx.currentProject = null;
      renderTour(unseen);
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });

    it("advances through the steps and remembers where it is", async () => {
      const user = userEvent.setup();
      renderTour(unseen);
      await screen.findByRole("dialog");

      await user.click(screen.getByText("tour.next"));

      expect(await screen.findByText("tour.steps.overview.title")).toBeInTheDocument();
      // Survives a hard reload mid-tour; the durable "seen it" answer stays on the member.
      expect(window.sessionStorage.getItem(STEP_STORAGE_KEY)).toBe("1");
    });

    it("goes back without losing the tour", async () => {
      const user = userEvent.setup();
      renderTour(unseen);
      await screen.findByRole("dialog");

      await user.click(screen.getByText("tour.next"));
      await screen.findByText("tour.steps.overview.title");
      await user.click(screen.getByText("tour.back"));

      expect(await screen.findByText("tour.steps.welcome.title")).toBeInTheDocument();
    });

    it("keeps going once the route leaves the project", async () => {
      // The last three steps are Inbox, Notifications and Account, which are cross-project:
      // `currentProject` is null there, and a tour derived from it would end early.
      const user = userEvent.setup();
      const { rerender } = renderTour(unseen);
      await screen.findByRole("dialog");
      await user.click(screen.getByText("tour.next"));
      await screen.findByText("tour.steps.overview.title");

      projectCtx.currentProject = null;
      rerender(
        <UserPreferencesProvider initial={unseen} tokenFactory={async () => "token"}>
          <ProductTour />
        </UserPreferencesProvider>
      );

      expect(screen.getByRole("dialog")).toBeInTheDocument();
      expect(screen.getByText("tour.steps.overview.title")).toBeInTheDocument();
    });

    it("records the skip on the member so it never comes back", async () => {
      const user = userEvent.setup();
      renderTour(unseen);
      await screen.findByRole("dialog");

      await user.click(screen.getByText("tour.skip"));

      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
      await waitFor(() =>
        expect(mockApi.org.updateMyPreferences).toHaveBeenCalledWith(
          { onboardingTourSeen: true },
          "token"
        )
      );
      expect(window.sessionStorage.getItem(STEP_STORAGE_KEY)).toBeNull();
    });

    it("comes back when Preferences clears the flag", async () => {
      const user = userEvent.setup();
      render(
        <UserPreferencesProvider initial={unseen} tokenFactory={async () => "token"}>
          <ReplayTrigger />
          <ProductTour />
        </UserPreferencesProvider>
      );
      await screen.findByRole("dialog");
      await user.click(screen.getByText("tour.skip"));
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

      // What the Replay button on the Preferences page does: clear the member flag.
      await user.click(screen.getByText("replay"));

      expect(await screen.findByRole("dialog")).toBeInTheDocument();
    });

    it("does not re-arm when the preference write fails", async () => {
      // The local flip is what stops the overlay — re-showing a tour someone just dismissed
      // because the network blipped is worse than losing the flag until the next reload.
      mockApi.org.updateMyPreferences.mockRejectedValueOnce(new Error("offline"));
      const user = userEvent.setup();
      renderTour(unseen);
      await screen.findByRole("dialog");

      await user.click(screen.getByText("tour.skip"));

      await waitFor(() => expect(mockApi.org.updateMyPreferences).toHaveBeenCalled());
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });
  });
});
