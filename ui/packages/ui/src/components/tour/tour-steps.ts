/**
 * The one-time product tour: what it visits, in what order, and what it points at.
 *
 * Steps are ordered to match the sidebar top-to-bottom, so the spotlight walks straight down
 * the nav instead of jumping around it. Every step anchors to a **sidebar item** rather than
 * to something inside the page: nav items are in the DOM the moment the shell renders, while
 * page content arrives whenever its data does — anchoring to that would spotlight empty space
 * on a slow request. A step whose anchor is missing (mobile, where the sidebar lives in a
 * closed drawer) falls back to a centered card, so the tour still explains every page.
 *
 * Copy lives in the `dashboard.tour.steps.<key>` namespace; keys deliberately mirror the nav
 * keys in `nav-model.ts` so a page added there has an obvious slot here.
 */

interface TourStep {
  key: string;
  /** Route the step explains. Undefined for the welcome card, which explains the tour itself. */
  href?: string;
  /** `data-tour` value to spotlight. Undefined → centered card with no spotlight. */
  anchor?: string;
}

/** Attribute name used to mark spotlight targets. */
const TOUR_ANCHOR_ATTR = "data-tour";

/** Spread onto a JSX element to make it a spotlight target: `{...tourAnchor("nav-board")}`. */
function tourAnchor(key: string): Record<string, string> {
  return { [TOUR_ANCHOR_ATTR]: key };
}

/**
 * The project pages, in sidebar order, then the cross-project ones below the divider.
 * `projectId` is the slug — the tour navigates with whatever the sidebar links to, so both
 * stay in step when a project is renamed.
 */
function getTourSteps(projectId: string): TourStep[] {
  const project = (path = "") => `/p/${projectId}${path}`;

  return [
    // No href: the welcome card appears wherever the user already is, so the tour opens
    // without a navigation the user did not ask for.
    { key: "welcome" },
    { key: "overview", href: project(), anchor: "nav-overview" },
    { key: "team", href: project("/team"), anchor: "nav-team" },
    { key: "activity", href: project("/activity"), anchor: "nav-activity" },
    { key: "skills", href: project("/skills"), anchor: "nav-skills" },
    { key: "automation", href: project("/automation"), anchor: "nav-automation" },
    { key: "memory", href: project("/memory"), anchor: "nav-memory" },
    { key: "roadmap", href: project("/roadmap"), anchor: "nav-roadmap" },
    { key: "board", href: project("/board"), anchor: "nav-board" },
    { key: "tasks", href: project("/tasks"), anchor: "nav-tasks" },
    { key: "repos", href: project("/repos"), anchor: "nav-repos" },
    { key: "documents", href: project("/documents"), anchor: "nav-documents" },
    { key: "design", href: project("/design"), anchor: "nav-design" },
    { key: "architect", href: project("/architect"), anchor: "nav-architect" },
    { key: "settings", href: project("/settings"), anchor: "nav-settings" },
    { key: "inbox", href: "/inbox", anchor: "nav-inbox" },
    { key: "notifications", href: "/notifications", anchor: "nav-notifications" },
    { key: "account", href: "/settings", anchor: "nav-account" },
  ];
}

export { getTourSteps, tourAnchor, TOUR_ANCHOR_ATTR };
export type { TourStep };
