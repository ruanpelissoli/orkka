import { invalidateApiCache } from "./api";

/**
 * The app-wide "data changed" signal.
 *
 * Every page loads its data in the browser (`useApiQuery` in apps/local). After a mutation the
 * visible UI must show the new state, which is what `revalidatePath` / `router.refresh()` did
 * when pages rendered on the server. `refreshData()` is the one replacement: it drops the API
 * client's GET cache and bumps a version every mounted page query subscribes to, so whatever
 * is on screen refetches (keeping its current data until the new response arrives — no
 * skeleton flash). Mutations in apps/local/lib/actions.ts call it; so does anything that
 * changed server state some other way (a chat turn that created documents, a repo linked).
 *
 * A plain store, not React state, so non-React code (actions, SignalR handlers) can fire it.
 */
let version = 0;
const listeners = new Set<() => void>();

export function refreshData(): void {
  invalidateApiCache();
  version += 1;
  for (const listener of listeners) listener();
}

export function subscribeDataRefresh(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function getDataVersion(): number {
  return version;
}
