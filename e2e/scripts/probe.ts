/** Read-only snapshot of the test workspace: workspaces, projects, tracker connections, runner. `npx tsx scripts/probe.ts` */
import { createApi } from "../src/auth/api-key.js";

const api = createApi();
const me = await api.org.me();
console.log("ME", JSON.stringify({ org: me.orgName, orgId: me.orgId, role: me.role, teamMembersEnabled: me.teamMembersEnabled, isPlatformAdmin: me.isPlatformAdmin, perms: me.permissions.length }));
console.log("WORKSPACES", JSON.stringify(await api.workspaces.list().catch((e) => `error ${(e as { status?: number }).status}`)));
const projects = await api.projects.list();
console.log("PROJECTS", JSON.stringify(projects.map((p) => ({ id: p.id, name: p.name, slug: p.slug, created: p.createdAt.slice(0, 10) })), null, 1));
console.log("TRACKER_CONNECTIONS", JSON.stringify(await api.trackers.connections().catch(() => [])));
const r = await api.me.runner();
console.log("RUNNER", JSON.stringify({ online: r.online, machine: r.machineName, v: r.daemonVersion, caps: r.capabilities, forgeClis: r.forgeClis, providers: (r.providers ?? r.lastKnownProviders ?? []).map((p) => `${p.provider}:${p.cliFound}/${p.authenticated}`) }));
