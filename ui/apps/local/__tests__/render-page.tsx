/**
 * Pages load their data in the browser: they render a skeleton (data-testid="page-loading")
 * until their queries resolve. renderPage renders one and waits for the skeleton to go.
 */
import { render, screen, waitFor, type RenderResult } from "@testing-library/react";
import { expect } from "vitest";
import { renderWithPermissions } from "./render-with-permissions";
import type { OrgMe } from "@/lib/types";

export async function waitForPage(): Promise<void> {
  await waitFor(() => expect(screen.queryByTestId("page-loading")).toBeNull());
}

export async function renderPage(ui: React.ReactElement): Promise<RenderResult> {
  const result = render(ui);
  await waitForPage();
  return result;
}

/** renderPage inside the PermissionsProvider (owner by default). */
export async function renderPageWithPermissions(
  ui: React.ReactElement,
  me?: OrgMe | null
): Promise<RenderResult> {
  const result = me === undefined ? renderWithPermissions(ui) : renderWithPermissions(ui, me);
  await waitForPage();
  return result;
}
