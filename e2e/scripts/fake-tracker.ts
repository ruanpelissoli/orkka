/**
 * Run the fake Jira Cloud + Azure DevOps server on its own, seeded with a small demo board, for
 * poking at a locally running runner/stack by hand. `npm run fake-tracker [-- --port 4010]`
 *
 * State: GET /__fake/state · requests: GET /__fake/requests · add a Jira issue:
 * POST /__fake/jira/issues {key, summary, parent?, status?} · add an ADO item: POST /__fake/ado/items
 * {type, title, parentId?} · outage: POST /__fake/down {down:true}.
 * The tokens are printed so they can be seeded into a runner sandbox (src/runner/tracker-seed.ts).
 */
import { env } from "../config/index.js";
import { FakeTrackerServer } from "../src/fakes/tracker/server.js";

const portArg = process.argv.indexOf("--port");
const port = portArg > 0 ? Number(process.argv[portArg + 1]) : env.E2E_FAKE_TRACKER_PORT || 4010;
const server = await new FakeTrackerServer({ port }).start();

server.jira.addEpic("SHOP-1", "Checkout redesign");
server.jira.addIssue({ key: "SHOP-10", summary: "Add a coupon field to checkout", parent: "SHOP-1", descriptionHtml: "<p>Customers can type a coupon code.</p>", acceptanceHtml: "<ul><li>A valid code applies the discount</li></ul>" });
server.ado.add({ id: 1, type: "Epic", title: "Checkout redesign" });
server.ado.add({ id: 10, type: "User Story", title: "Add a coupon field to checkout", parentId: 1, descriptionHtml: "<div>Customers can type a coupon code.</div>" });

console.log(`fake tracker listening on ${server.origin}`);
console.log(`  Jira  site ${server.jiraSiteUrl}  email ${server.jira.email}  token ${server.jira.token}  project SHOP, epic SHOP-1`);
console.log(`  ADO   site ${server.adoSiteUrl}  pat ${server.ado.pat}  project Shop, epic 1`);
console.log("Ctrl+C to stop.");
process.on("SIGINT", () => void server.stop().then(() => process.exit(0)));
