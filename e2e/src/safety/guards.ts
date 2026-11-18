import { cfg } from "../../config/index.js";
import { installSecret } from "../local/stack.js";
import type { OrkkaApi } from "../api/client.js";
import type { ProjectResponse } from "../api/types.js";

export class SafetyViolation extends Error {
  constructor(message: string) {
    super(`SAFETY: ${message}`);
    this.name = "SafetyViolation";
  }
}

export function isE2eName(name: string): boolean {
  return name.startsWith(cfg.projectPrefix);
}

export function assertE2eProject(project: Pick<ProjectResponse, "id" | "name">): void {
  if (!isE2eName(project.name)) {
    throw new SafetyViolation(`refusing to touch project "${project.name}" (${project.id}) — only "${cfg.projectPrefix}*" projects may be modified or deleted by the harness`);
  }
}

/**
 * The stack behind the credentials must be the harness's SANDBOX — never the developer's own
 * Orkka, which listens on the same 127.0.0.1:6750 when it runs. Proof: the host is on loopback
 * and accepts the sandbox's install secret (each install generates its own).
 */
export async function assertSandboxStack(api: OrkkaApi): Promise<{ orgId: string; orgName: string }> {
  const host = new URL(cfg.apiUrl).hostname;
  if (!["127.0.0.1", "localhost", "[::1]"].includes(host)) throw new SafetyViolation(`E2E_API_URL=${cfg.apiUrl} is not a loopback local stack`);
  const r = await fetch(new URL("/local/launch-codes", cfg.apiUrl), { method: "POST", headers: { Authorization: `Bearer ${installSecret()}` } }).catch(() => undefined);
  if (r?.status !== 200) throw new SafetyViolation(`the Orkka at ${cfg.apiUrl} does not accept the sandbox's install secret (${r?.status ?? "unreachable"}) — it is not the harness's stack (is your own Orkka running? stop it, then npm run stack:start)`);
  const me = await api.org.me();
  return { orgId: me.orgId, orgName: me.orgName };
}

/** Delete a project by id after fetching it and checking the name guard and the org. */
export async function deleteProject(api: OrkkaApi, projectId: string): Promise<ProjectResponse | undefined> {
  let project: ProjectResponse;
  try {
    project = await api.projects.get(projectId);
  } catch (e) {
    if ((e as { status?: number }).status === 404) return undefined;
    throw e;
  }
  assertE2eProject(project);
  await assertSandboxStack(api);
  await api.projects.deleteUnguarded(project.id);
  return project;
}
