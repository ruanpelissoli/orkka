/**
 * Commands a developer types at the runner, in one place so the settings pages and the
 * tracker cards never drift apart. Mirrors the ./orkka CLI README (`tracker add`).
 * Installing and starting the runner is the launcher's job (`./orkka start`).
 */
import { TRACKER_ADD_COMMANDS } from "./tracker";

/** A workspace name the CLI takes as one shell word. */
export function workspaceArg(slug: string): string {
  return /^[A-Za-z0-9._-]+$/.test(slug) ? slug : `"${slug.replace(/"/g, "")}"`;
}

/** `./orkka tracker add jira|ado …`, scoped to a workspace when one is given. */
export function trackerAddCommand(kind: "jira" | "ado", workspace?: string | null): string {
  const base = TRACKER_ADD_COMMANDS[kind];
  return workspace ? `${base} --workspace ${workspaceArg(workspace)}` : base;
}
