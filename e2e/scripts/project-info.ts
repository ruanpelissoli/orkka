/** Print one project's state (read-only). `npx tsx scripts/project-info.ts <idOrSlug>` */
import { createApi } from "../src/auth/api-key.js";

const id = process.argv[2];
if (!id) throw new Error("usage: project-info <idOrSlug>");
const api = createApi();
const p = await api.projects.get(id);
console.log(JSON.stringify({ id: p.id, name: p.name, slug: p.slug, settings: p.settings }, null, 1));
const repos = await api.repos.list(p.id);
console.log("REPOS", JSON.stringify(repos.map((r) => ({ id: r.id, name: r.name, provider: r.provider, gitUrl: r.gitUrl, defaultBranch: r.defaultBranch, cliAccount: r.cliAccount, workspaceStatus: r.workspaceStatus }))));
console.log("TICKET_SOURCE", JSON.stringify(await api.projects.tracker.get(p.id), null, 1));
