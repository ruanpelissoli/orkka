import { vi } from "vitest";

// ── @/lib/navigation ────────────────────────────────────────────────
// Pages read route params with useParams() and the query string with useSearchParams(), and
// navigate with the locale-aware router (mocks/i18n-navigation.ts).
export const mockUseSearchParams = vi.fn(() => new URLSearchParams());
export const mockUseParams = vi.fn((): Record<string, string> => ({}));

/** Query-string updates land in the real (happy-dom) URL, as the router's would. */
export const mockUpdateSearchParams = vi.fn((update: (params: URLSearchParams) => void) => {
  const url = new URL(window.location.href);
  update(url.searchParams);
  window.history.pushState(null, "", url);
});

vi.mock("@/lib/navigation", () => ({
  useSearchParams: mockUseSearchParams,
  useParams: mockUseParams,
  useUpdateSearchParams: () => mockUpdateSearchParams,
}));

/** Route params and query string for the page under test. */
export function setRoute(params: Record<string, string>, search = "") {
  mockUseParams.mockReturnValue(params);
  mockUseSearchParams.mockReturnValue(new URLSearchParams(search));
}
