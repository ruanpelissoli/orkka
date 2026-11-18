/**
 * A full page load. Switching workspace uses it on purpose: every page query, client
 * cache, SignalR group and in-memory store is rebuilt for the new workspace, so nothing from
 * the previous job can show for a frame. A module of its own so tests can stub it.
 */
export function hardNavigate(url: string): void {
  window.location.assign(url);
}
