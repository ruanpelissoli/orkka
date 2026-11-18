/**
 * The navigation model moved to core (`packages/core/src/lib/nav-model.ts`)
 * so the React Native app can share the exact same IA. This module stays as the
 * app-local import path used by DashboardShell/Sidebar/MobileTabBar.
 */
export { getProjectNav, getGlobalNav, getMobileTabs } from "@/lib/nav-model";
export type { NavTranslator } from "@/lib/nav-model";
