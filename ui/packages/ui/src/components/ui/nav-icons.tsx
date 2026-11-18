import type { NavIconName, NavItem, MobileTab } from "@/lib/nav-model";

/**
 * Shared navigation icon set, consumed by the app's Sidebar and the shared
 * MobileTabBar/MobileDrawer. The nav item types (and the nav model)
 * live in core's `lib/nav-model.ts` so non-DOM clients can share them; they are re-exported
 * from here so existing `@/components/ui` imports keep working.
 */

const PATHS: Record<NavIconName, React.ReactNode> = {
  home: (
    <>
      <path d="M3 9l9-7 9 7v11a2 2 0 01-2 2H5a2 2 0 01-2-2z" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M9 22V12h6v10" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  board: (
    <>
      <rect x="3" y="3" width="7" height="9" rx="1" strokeLinecap="round" strokeLinejoin="round" />
      <rect x="14" y="3" width="7" height="5" rx="1" strokeLinecap="round" strokeLinejoin="round" />
      <rect x="14" y="12" width="7" height="9" rx="1" strokeLinecap="round" strokeLinejoin="round" />
      <rect x="3" y="16" width="7" height="5" rx="1" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  // Stacked progress bars — the roadmap is milestones by completion, not a calendar.
  roadmap: (
    <>
      <rect x="3" y="4" width="18" height="4" rx="2" strokeLinecap="round" strokeLinejoin="round" />
      <rect x="3" y="10" width="12" height="4" rx="2" strokeLinecap="round" strokeLinejoin="round" />
      <rect x="3" y="16" width="7" height="4" rx="2" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  tasks: (
    <>
      <path d="M9 11l3 3L22 4" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M21 12v7a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2h11" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  repos: (
    <>
      <circle cx="6" cy="6" r="2" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="6" cy="18" r="2" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="18" cy="8" r="2" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M6 8v8M18 10c0 4-6 3-11 6.5" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  documents: (
    <>
      <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M14 2v6h6M16 13H8M16 17H8M10 9H8" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  design: (
    <>
      <rect x="3" y="3" width="18" height="18" rx="2" strokeLinecap="round" />
      <circle cx="9" cy="9" r="2" />
      <path d="M21 15l-3.5-3.5a1.5 1.5 0 00-2.1 0L7 20" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  team: (
    <path
      d="M12 2a2 2 0 100 4 2 2 0 000-4zM4.5 7a1.5 1.5 0 100 3 1.5 1.5 0 000-3zM19.5 7a1.5 1.5 0 100 3 1.5 1.5 0 000-3zM16 21v-2a4 4 0 00-4-4h0a4 4 0 00-4 4v2M21 21v-1a3 3 0 00-2-2.83M3 21v-1a3 3 0 012-2.83"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
  ),
  architect: (
    <>
      <rect x="3" y="3" width="7" height="7" rx="1" strokeLinecap="round" />
      <rect x="14" y="3" width="7" height="7" rx="1" strokeLinecap="round" />
      <rect x="8" y="14" width="8" height="7" rx="1" strokeLinecap="round" />
      <path d="M10 6.5h4M6.5 10v4l5.5 0M17.5 10v4l-5.5 0" strokeLinecap="round" />
    </>
  ),
  members: (
    <>
      <path d="M17 21v-2a4 4 0 00-4-4H5a4 4 0 00-4 4v2" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="9" cy="7" r="4" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M23 21v-2a4 4 0 00-3-3.87M16 3.13a4 4 0 010 7.75" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  settings: (
    <>
      <path
        d="M12.22 2h-.44a2 2 0 00-2 2v.18a2 2 0 01-1 1.73l-.43.25a2 2 0 01-2 0l-.15-.08a2 2 0 00-2.73.73l-.22.38a2 2 0 00.73 2.73l.15.1a2 2 0 011 1.72v.51a2 2 0 01-1 1.74l-.15.09a2 2 0 00-.73 2.73l.22.38a2 2 0 002.73.73l.15-.08a2 2 0 012 0l.43.25a2 2 0 011 1.73V20a2 2 0 002 2h.44a2 2 0 002-2v-.18a2 2 0 011-1.73l.43-.25a2 2 0 012 0l.15.08a2 2 0 002.73-.73l.22-.39a2 2 0 00-.73-2.73l-.15-.08a2 2 0 01-1-1.74v-.5a2 2 0 011-1.74l.15-.09a2 2 0 00.73-2.73l-.22-.38a2 2 0 00-2.73-.73l-.15.08a2 2 0 01-2 0l-.43-.25a2 2 0 01-1-1.73V4a2 2 0 00-2-2z"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <circle cx="12" cy="12" r="3" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  bell: (
    <path
      d="M18 8A6 6 0 006 8c0 7-3 9-3 9h18s-3-2-3-9M13.73 21a2 2 0 01-3.46 0"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
  ),
  user: (
    <>
      <path d="M20 21v-2a4 4 0 00-4-4H8a4 4 0 00-4 4v2" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="12" cy="7" r="4" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  terminal: (
    <>
      <rect x="2" y="4" width="20" height="16" rx="2" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M6 9l3 3-3 3M12 15h6" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  inbox: (
    <>
      <path d="M4 13h4l2 3h4l2-3h4" strokeLinecap="round" strokeLinejoin="round" />
      <path
        d="M5.5 4h13l2.5 9v5a2 2 0 01-2 2H5a2 2 0 01-2-2v-5z"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </>
  ),
  // Heartbeat trace — the activity timeline is "what ran, when".
  activity: <path d="M22 12h-4l-3 9L9 3l-3 9H2" strokeLinecap="round" strokeLinejoin="round" />,
  // A book with a bookmark — skills are the team's playbooks.
  skills: (
    <>
      <path d="M4 19.5A2.5 2.5 0 016.5 17H20" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M6.5 2H20v20H6.5A2.5 2.5 0 014 19.5v-15A2.5 2.5 0 016.5 2z" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M12 2v9l2.5-1.5L17 11V2" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  // A clock — automation is "when things run on their own".
  automation: (
    <>
      <circle cx="12" cy="12" r="9" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M12 7v5l3 2" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  // A brain-ish loop — memory is what the team keeps between runs.
  memory: (
    <>
      <path d="M9.5 3a3.5 3.5 0 00-3.4 4.3A3.5 3.5 0 004 11a3.5 3.5 0 002 6.3V18a3 3 0 006 0V6a3 3 0 00-2.5-3z" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M14.5 3a3.5 3.5 0 013.4 4.3A3.5 3.5 0 0120 11a3.5 3.5 0 01-2 6.3V18a3 3 0 01-6 0V6a3 3 0 012.5-3z" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  plus: <path d="M12 5v14m-7-7h14" strokeLinecap="round" />,
};

function NavIcon({
  name,
  size = 18,
  strokeWidth = 1.5,
}: {
  name: NavIconName;
  size?: number;
  strokeWidth?: number;
}) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={strokeWidth}>
      {PATHS[name]}
    </svg>
  );
}

export { NavIcon };
export type { NavIconName, NavItem, MobileTab };
