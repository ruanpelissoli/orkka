import { test, expect } from "../../src/fixtures/test.js";
import { basicOf, type FakeTrackerServer } from "../../src/fakes/tracker/server.js";

/**
 * Self-tests of the fake Jira Cloud / Azure DevOps server: each request below is the one
 * the runner's JiraCloudClient / AzureDevOpsClient / TrackerAttachmentCache / TrackerWriteBack
 * sends (same path, query, body and Basic auth), and each assertion is on a field the runner
 * parses. No Orkka deployment needed. @offline
 */

async function call(server: FakeTrackerServer, method: string, url: string, auth: string | null, body?: unknown, contentType = "application/json") {
  const res = await fetch(url.startsWith("http") ? url : `${server.origin}${url}`, {
    method,
    redirect: "manual",
    headers: { Accept: "application/json", "User-Agent": "orkka", ...(auth ? { Authorization: auth } : {}), ...(body !== undefined ? { "Content-Type": contentType } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const type = res.headers.get("content-type") ?? "";
  const text = await res.text();
  return { status: res.status, type, location: res.headers.get("location"), json: type.includes("json") && text ? JSON.parse(text) : undefined, text };
}

const jiraSeed = (server: FakeTrackerServer) => {
  const j = server.jira;
  j.addEpic("SHOP-1", "Checkout redesign");
  j.addEpic("SHOP-2", "Search");
  j.addIssue({ key: "SHOP-10", summary: "Add coupon field", parent: "SHOP-1", descriptionHtml: "<p>Coupons <b>now</b></p>", acceptanceHtml: "<ul><li>valid code applies</li></ul>", labels: ["web"] });
  j.addIssue({ key: "SHOP-11", summary: "Done already", parent: "SHOP-1", status: "Done" });
  j.addIssue({ key: "SHOP-12", summary: "Someone else's", parent: "SHOP-1", assignee: "colleague" });
  j.addIssue({ key: "SHOP-13", summary: "Sub-epic story", parent: "SHOP-10", type: "Sub-task" });
  j.addIssue({ key: "SHOP-20", summary: "Search box", parent: "SHOP-2", labels: ["api"] });
};

test.describe("fake Jira Cloud @offline", () => {
  test("auth: /myself answers for email+token and 401s anything else", async ({ fakeTracker: s }) => {
    const ok = await call(s, "GET", "/rest/api/3/myself", s.jira.authorization);
    expect(ok.status).toBe(200);
    expect(ok.json).toMatchObject({ accountId: s.jira.accountId, displayName: s.jira.displayName });
    const bad = await call(s, "GET", "/rest/api/3/myself", basicOf(s.jira.email, "wrong"));
    expect(bad.status).toBe(401);
    expect(bad.json.errorMessages[0]).toMatch(/authenticated/);
    expect(s.requestsTo("jira").map((r) => r.authOk)).toEqual([true, false]);
  });

  test("scopes, statuses and the Acceptance Criteria field lookup", async ({ fakeTracker: s }) => {
    const a = s.jira.authorization;
    const projects = await call(s, "GET", "/rest/api/3/project/search?maxResults=100&startAt=0&orderBy=name", a);
    expect(projects.json).toMatchObject({ isLast: true, values: [{ key: "SHOP", name: "Shop" }] });
    const statuses = await call(s, "GET", "/rest/api/3/project/SHOP/statuses", a);
    const task = statuses.json.find((t: { name: string }) => t.name === "Task");
    expect(task.statuses.map((x: { name: string; statusCategory: { key: string } }) => `${x.name}:${x.statusCategory.key}`)).toEqual(["To Do:new", "In Progress:indeterminate", "In Review:indeterminate", "Done:done"]);
    const fields = await call(s, "GET", "/rest/api/3/field", a);
    expect(fields.json.find((f: { name: string }) => f.name === "Acceptance Criteria")?.id).toBe("customfield_10050");
    expect((await call(s, "GET", "/rest/api/3/project/NOPE/statuses", a)).status).toBe(404);
  });

  test("epic search: by title prefix, by key, by bare number, unknown key → 400", async ({ fakeTracker: s }) => {
    jiraSeed(s);
    const a = s.jira.authorization;
    const search = (jql: string) => call(s, "POST", "/rest/api/3/search/jql", a, { jql, fields: ["summary", "status"], maxResults: 20 });
    const all = await search(`project = "SHOP" AND issuetype = Epic ORDER BY updated DESC`);
    expect(all.json.issues.map((i: { key: string }) => i.key).sort()).toEqual(["SHOP-1", "SHOP-2"]);
    const byTitle = await search(`project = "SHOP" AND issuetype = Epic AND summary ~ "check*" ORDER BY updated DESC`);
    expect(byTitle.json.issues.map((i: { key: string; fields: { summary: string } }) => i.fields.summary)).toEqual(["Checkout redesign"]);
    const byKey = await search(`project = "SHOP" AND issuetype = Epic AND key = "SHOP-2" ORDER BY updated DESC`);
    expect(byKey.json.issues[0].key).toBe("SHOP-2");
    const missing = await search(`project = "SHOP" AND issuetype = Epic AND key = "SHOP-999" ORDER BY updated DESC`);
    expect(missing.status).toBe(400);
  });

  test("polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging", async ({ fakeTracker: s }) => {
    jiraSeed(s);
    const a = s.jira.authorization;
    const fields = ["summary", "issuetype", "status", "priority", "labels", "description", "attachment", "updated", "parent", "customfield_10050"];
    const jql = `parent in ("SHOP-1", "SHOP-2") AND assignee = currentUser() AND statusCategory != Done ORDER BY key ASC`;
    const page1 = await call(s, "POST", "/rest/api/3/search/jql", a, { jql, fields, maxResults: 1, expand: "renderedFields" });
    expect(page1.json.issues.map((i: { key: string }) => i.key)).toEqual(["SHOP-10"]);
    expect(page1.json.isLast).toBe(false);
    const first = page1.json.issues[0];
    expect(first.fields.parent.key).toBe("SHOP-1");
    expect(first.fields.status).toMatchObject({ name: "To Do", statusCategory: { key: "new" } });
    expect(first.renderedFields.description).toBe("<p>Coupons <b>now</b></p>");
    expect(first.renderedFields.customfield_10050).toContain("valid code applies");
    expect(first.fields.updated).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}\+0000$/);
    const page2 = await call(s, "POST", "/rest/api/3/search/jql", a, { jql, fields, maxResults: 1, nextPageToken: page1.json.nextPageToken, expand: "renderedFields" });
    expect(page2.json.issues.map((i: { key: string }) => i.key)).toEqual(["SHOP-20"]);
    expect(page2.json.isLast).toBe(true);

    const extra = `parent in ("SHOP-1", "SHOP-2") AND assignee = currentUser() AND statusCategory != Done AND (labels = "api")`;
    expect((await call(s, "POST", "/rest/api/3/search/approximate-count", a, { jql: extra })).json).toEqual({ count: 1 });
    const children = await call(s, "POST", "/rest/api/3/search/jql", a, { jql: `parent in ("SHOP-1")`, fields: ["issuetype", "parent"], maxResults: 100 });
    expect(children.json.issues.map((i: { key: string }) => i.key)).toEqual(["SHOP-10", "SHOP-11", "SHOP-12"]);
    const unsupported = await call(s, "POST", "/rest/api/3/search/jql", a, { jql: `parent in ("SHOP-1") AND (sprint in openSprints())`, fields, maxResults: 10 });
    expect(unsupported.status).toBe(400);
    expect(unsupported.json.errorMessages[0]).toMatch(/does not understand/);
  });

  test("intake v2: key in (…) tracking, a missing key fails the query like Jira; subtasks, assignee, components", async ({ fakeTracker: s }) => {
    jiraSeed(s);
    s.jira.addIssue({ key: "SHOP-30", type: "Story", summary: "A story" });
    s.jira.addIssue({ key: "SHOP-31", type: "Sub-task", parent: "SHOP-30", summary: "First", components: ["api"] });
    s.jira.addIssue({ key: "SHOP-32", type: "Sub-task", parent: "SHOP-30", summary: "Second", assignee: "colleague" });
    const a = s.jira.authorization;
    const search = (jql: string) => call(s, "POST", "/rest/api/3/search/jql", a, { jql, fields: ["summary", "issuetype", "status", "parent", "subtasks", "assignee", "components"], maxResults: 100 });
    const tracked = await search("key in (\"SHOP-31\", \"SHOP-32\") ORDER BY key ASC");
    expect(tracked.json.issues.map((i: { key: string }) => i.key)).toEqual(["SHOP-31", "SHOP-32"]);
    const missing = await search("key in (\"SHOP-31\", \"SHOP-99\")");
    expect(missing.status).toBe(400);
    expect(missing.json.errorMessages[0]).toMatch(/An issue with key 'SHOP-99' does not exist for field 'key'/);
    const story = (await search("key in (\"SHOP-30\")")).json.issues[0];
    expect(story.fields.subtasks.map((x: { key: string }) => x.key)).toEqual(["SHOP-31", "SHOP-32"]);
    expect(story.fields.issuetype).toMatchObject({ name: "Story", subtask: false, hierarchyLevel: 0 });
    const [first, second] = tracked.json.issues;
    expect(first.fields.assignee.accountId).toBe(s.jira.accountId);
    expect(second.fields.assignee.accountId).not.toBe(s.jira.accountId);
    expect(first.fields.components).toEqual([{ id: "10100", name: "api" }]);
    expect(first.fields.parent).toMatchObject({ key: "SHOP-30", fields: { issuetype: { name: "Story" } } });
    expect(first.fields.issuetype).toMatchObject({ subtask: true, hierarchyLevel: -1 });
  });

  test("comments newest-first with rendered bodies; attachments via a 303 to the media host", async ({ fakeTracker: s }) => {
    jiraSeed(s);
    const a = s.jira.authorization;
    s.jira.addComment("SHOP-10", "<p>first</p>", "Ana");
    s.jira.addComment("SHOP-10", "<p>second</p>", "Bea");
    const att = s.jira.addAttachment("SHOP-10", { filename: "mock.png", mimeType: "image/png", content: Buffer.from([0x89, 0x50, 0x4e, 0x47]) });
    const comments = await call(s, "GET", "/rest/api/3/issue/SHOP-10/comment?orderBy=-created&maxResults=1&expand=renderedBody", a);
    expect(comments.json.comments).toHaveLength(1);
    expect(comments.json.comments[0]).toMatchObject({ author: { displayName: "Bea" }, renderedBody: "<p>second</p>" });

    const search = await call(s, "POST", "/rest/api/3/search/jql", a, { jql: `parent in ("SHOP-1") AND assignee = currentUser() AND statusCategory != Done`, fields: ["attachment"], maxResults: 10 });
    const listed = search.json.issues[0].fields.attachment[0];
    expect(listed).toMatchObject({ id: att.id, filename: "mock.png", size: 4, mimeType: "image/png" });
    expect(listed.content).toBe(`${s.origin}/rest/api/3/attachment/content/${att.id}`);
    const redirect = await call(s, "GET", listed.content, a);
    expect(redirect.status).toBe(303);
    const media = await fetch(redirect.location!);
    expect(Buffer.from(await media.arrayBuffer())).toEqual(Buffer.from([0x89, 0x50, 0x4e, 0x47]));
  });

  test("write-back: status read, transitions list, POST transition 204, invalid id 400", async ({ fakeTracker: s }) => {
    jiraSeed(s);
    const a = s.jira.authorization;
    const current = await call(s, "GET", "/rest/api/3/issue/SHOP-10?fields=status", a);
    expect(current.json.fields.status.name).toBe("To Do");
    const list = await call(s, "GET", "/rest/api/3/issue/SHOP-10/transitions", a);
    const toProgress = list.json.transitions.find((t: { to: { name: string } }) => t.to.name === "In Progress");
    expect(toProgress.id).toBeTruthy();
    const moved = await call(s, "POST", "/rest/api/3/issue/SHOP-10/transitions", a, { transition: { id: toProgress.id } });
    expect(moved.status).toBe(204);
    expect(s.jira.issue("SHOP-10").status).toBe("In Progress");
    expect(s.transitions).toMatchObject([{ tracker: "jira", key: "SHOP-10", from: "To Do", to: "In Progress" }]);
    expect((await call(s, "POST", "/rest/api/3/issue/SHOP-10/transitions", a, { transition: { id: "999" } })).status).toBe(400);
    expect((await call(s, "GET", "/rest/api/3/issue/SHOP-404?fields=status", a)).status).toBe(404);
    // A restricted workflow hides the move, which the runner reports as "no transition to …".
    s.jira.workflow = { "In Progress": ["In Review"] };
    const restricted = await call(s, "GET", "/rest/api/3/issue/SHOP-10/transitions", a);
    expect(restricted.json.transitions.map((t: { to: { name: string } }) => t.to.name)).toEqual(["In Review"]);
  });

  test("boards: a project's boards, a board's filter, its JQL (ORDER BY stripped) AND-ed onto intake; a gone board 404s", async ({ fakeTracker: s }) => {
    jiraSeed(s);
    s.jira.update("SHOP-10", { components: ["Web"] });
    s.jira.update("SHOP-20", { components: ["Api"] });
    const a = s.jira.authorization;
    const web = s.jira.addBoard({ name: "Web board", jql: "project = SHOP AND component = Web ORDER BY Rank ASC" });
    s.jira.addBoard({ name: "Api board", jql: "project = SHOP AND component in (Api) ORDER BY Rank ASC", type: "kanban" });
    s.jira.addBoard({ name: "Other", jql: "project = OPS", project: "OPS" });

    // The runner's Board select: GET /rest/agile/1.0/board, paged by startAt.
    const page1 = await call(s, "GET", "/rest/agile/1.0/board?projectKeyOrId=SHOP&maxResults=1&startAt=0", a);
    expect(page1.json).toMatchObject({ isLast: false, values: [{ id: web.id, name: "Web board", type: "scrum" }] });
    const page2 = await call(s, "GET", "/rest/agile/1.0/board?projectKeyOrId=SHOP&maxResults=1&startAt=1", a);
    expect(page2.json).toMatchObject({ isLast: true, values: [{ name: "Api board", type: "kanban" }] });

    // Each poll: board configuration → filter → its JQL.
    const conf = await call(s, "GET", `/rest/agile/1.0/board/${web.id}/configuration`, a);
    expect(conf.json.filter.id).toBe(web.filterId);
    const filter = await call(s, "GET", `/rest/api/3/filter/${web.filterId}`, a);
    expect(filter.json.jql).toBe("project = SHOP AND component = Web ORDER BY Rank ASC");

    // The intake query the runner builds with the board's clause (JiraQueries.IntakeJql).
    const intake = await call(s, "POST", "/rest/api/3/search/jql", a, {
      jql: `project = "SHOP" AND assignee = currentUser() AND statusCategory = "To Do" AND (project = SHOP AND component = Web) ORDER BY key ASC`,
      fields: ["summary"],
      maxResults: 100,
    });
    expect(intake.json.issues.map((i: { key: string }) => i.key)).toEqual(["SHOP-10"]);

    s.jira.removeBoard(web.id);
    expect((await call(s, "GET", `/rest/agile/1.0/board/${web.id}/configuration`, a)).status).toBe(404);
    const hidden = s.jira.addBoard({ name: "Private", jql: "project = SHOP", shared: false });
    expect((await call(s, "GET", `/rest/api/3/filter/${hidden.filterId}`, a)).status).toBe(404);
    expect((await call(s, "GET", "/rest/agile/1.0/board?projectKeyOrId=SHOP&maxResults=50&startAt=0", basicOf(s.jira.email, "wrong"))).status).toBe(401);
  });

  test("failure injection and an outage", async ({ fakeTracker: s }) => {
    const a = s.jira.authorization;
    s.injectFailure({ tracker: "jira", path: /\/search\/jql$/, status: 500, times: 1 });
    expect((await call(s, "POST", "/rest/api/3/search/jql", a, { jql: "project = SHOP", fields: [], maxResults: 1 })).status).toBe(500);
    expect((await call(s, "POST", "/rest/api/3/search/jql", a, { jql: "project = SHOP", fields: [], maxResults: 1 })).status).toBe(200);
    s.down = true;
    expect((await call(s, "GET", "/rest/api/3/myself", a)).status).toBe(503);
  });
});

test.describe("fake Azure DevOps @offline", () => {
  const seed = (s: FakeTrackerServer) => {
    const d = s.ado;
    d.add({ id: 1, type: "Epic", title: "Checkout redesign" });
    d.add({ id: 2, type: "Feature", title: "Coupons", parentId: 1 });
    d.add({ id: 10, type: "User Story", title: "Coupon field", parentId: 2, descriptionHtml: "<div>Coupons</div>", acceptanceHtml: "<div>applies</div>", tags: ["web", "ui"] });
    d.add({ id: 11, type: "Bug", title: "Rounding bug", parentId: 1, reproHtml: "<div>1. add 0.1</div>" });
    d.add({ id: 12, type: "Task", title: "Closed task", parentId: 1, state: "Closed" });
    d.add({ id: 13, type: "Task", title: "Not mine", parentId: 1, assignedToMe: false });
  };
  const site = (s: FakeTrackerServer) => s.adoSiteUrl;

  test("auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service", async ({ fakeTracker: s }) => {
    const ok = await call(s, "GET", `${site(s)}/_apis/connectionData`, s.ado.authorization);
    expect(ok.json.authenticatedUser).toMatchObject({ id: s.ado.userId, providerDisplayName: s.ado.displayName });
    const bad = await call(s, "GET", `${site(s)}/_apis/connectionData`, basicOf("", "nope"));
    expect(bad.status).toBe(203);
    expect(bad.type).toContain("text/html");
  });

  test("projects, work item types with state categories, type categories", async ({ fakeTracker: s }) => {
    const a = s.ado.authorization;
    expect((await call(s, "GET", `${site(s)}/_apis/projects?$top=200&api-version=7.1`, a)).json.value.map((p: { name: string }) => p.name)).toEqual(["Shop"]);
    const types = await call(s, "GET", `${site(s)}/Shop/_apis/wit/workitemtypes?api-version=7.1`, a);
    const story = types.json.value.find((t: { name: string }) => t.name === "User Story");
    expect(story.states.find((x: { name: string }) => x.name === "Closed").category).toBe("Completed");
    const cats = await call(s, "GET", `${site(s)}/Shop/_apis/wit/workitemtypecategories?api-version=7.1`, a);
    expect(cats.json.value.find((c: { referenceName: string }) => c.referenceName === "Microsoft.RequirementCategory").workItemTypes).toEqual([{ name: "User Story" }]);
    expect((await call(s, "GET", `${site(s)}/Nope/_apis/wit/workitemtypes?api-version=7.1`, a)).status).toBe(404);
  });

  test("two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations", async ({ fakeTracker: s }) => {
    seed(s);
    const a = s.ado.authorization;
    const wiql = (query: string, top?: number) => call(s, "POST", `${site(s)}/Shop/_apis/wit/wiql?${top ? `$top=${top}&` : ""}api-version=7.1`, a, { query });
    const direct = await wiql("SELECT [System.Id] FROM WorkItemLinks WHERE [Source].[System.Id] IN (1) AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward' MODE (MustContain)");
    const targets = (r: { json: { workItemRelations: { source: unknown; target: { id: number } }[] } }) => r.json.workItemRelations.filter((x) => x.source).map((x) => x.target.id).sort((p, q) => p - q);
    expect(targets(direct)).toEqual([2, 11, 12, 13]);
    const tree = await wiql("SELECT [System.Id] FROM WorkItemLinks WHERE [Source].[System.Id] IN (1) AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward' MODE (Recursive)");
    expect(targets(tree)).toEqual([2, 10, 11, 12, 13]);
    expect(tree.json.workItemRelations[0]).toMatchObject({ rel: null, source: null, target: { id: 1 } });

    const flat = await wiql("SELECT [System.Id] FROM WorkItems WHERE [System.Id] IN (2, 10, 11, 12, 13) AND [System.AssignedTo] = @Me AND [System.State] NOT IN ('Closed', 'Removed') AND ([System.Tags] CONTAINS 'web') ORDER BY [System.Id]");
    expect(flat.json.workItems.map((w: { id: number }) => w.id)).toEqual([10]);

    const batch = await call(s, "POST", `${site(s)}/_apis/wit/workitemsbatch?api-version=7.1`, a, { ids: [10, 11], $expand: "Relations", errorPolicy: "Omit" });
    const story = batch.json.value.find((v: { id: number }) => v.id === 10);
    expect(story.fields).toMatchObject({ "System.Title": "Coupon field", "System.WorkItemType": "User Story", "System.State": "New", "System.Tags": "web; ui", "Microsoft.VSTS.Common.AcceptanceCriteria": "<div>applies</div>" });
    expect(story.relations.find((r: { rel: string }) => r.rel === "System.LinkTypes.Hierarchy-Reverse").url).toMatch(/\/workItems\/2$/);
    expect((await call(s, "POST", `${site(s)}/_apis/wit/workitemsbatch?api-version=7.1`, a, { ids: [10], fields: ["System.Title"], $expand: "Relations" })).status).toBe(400);

    const epics = await wiql("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] = 'Epic' AND [System.Title] CONTAINS 'check' ORDER BY [System.ChangedDate] DESC", 20);
    expect(epics.json.workItems.map((w: { id: number }) => w.id)).toEqual([1]);
    expect((await wiql("SELECT [System.Id] FROM WorkItems WHERE [Custom.Field] = 'x'")).status).toBe(400);

    // Intake v2 skips containers by type.
    const intake = await wiql("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me AND [System.State] IN ('New') AND [System.WorkItemType] NOT IN ('Epic', 'Feature') ORDER BY [System.Id]");
    expect(intake.status).toBe(200);
    expect(intake.json.workItems.map((w: { id: number }) => w.id)).not.toContain(1);
  });

  test("comments (desc, deleted skipped by the runner), attachments, System.State write-back", async ({ fakeTracker: s }) => {
    seed(s);
    const a = s.ado.authorization;
    s.ado.addComment(10, "<div>hello</div>", "Ana");
    const att = s.ado.addAttachment(10, { filename: "spec.txt", mimeType: "text/plain", content: Buffer.from("spec") });
    const comments = await call(s, "GET", `${site(s)}/Shop/_apis/wit/workItems/10/comments?$top=20&order=desc&$expand=renderedText&api-version=7.1-preview.4`, a);
    expect(comments.json.comments[0]).toMatchObject({ id: expect.any(Number), renderedText: "<div>hello</div>", createdBy: { displayName: "Ana" }, isDeleted: false });

    const batch = await call(s, "POST", `${site(s)}/_apis/wit/workitemsbatch?api-version=7.1`, a, { ids: [10], $expand: "Relations", errorPolicy: "Omit" });
    const rel = batch.json.value[0].relations.find((r: { rel: string }) => r.rel === "AttachedFile");
    expect(rel.attributes).toEqual({ name: "spec.txt", resourceSize: 4 });
    expect(rel.url).toContain(att.guid);
    expect((await call(s, "GET", rel.url, a)).text).toBe("spec");

    const state = await call(s, "GET", `${site(s)}/_apis/wit/workitems/10?fields=System.State&api-version=7.1`, a);
    expect(state.json.fields).toEqual({ "System.State": "New" });
    const patch = await call(s, "PATCH", `${site(s)}/_apis/wit/workitems/10?api-version=7.1`, a, [{ op: "add", path: "/fields/System.State", value: "Active" }], "application/json-patch+json");
    expect(patch.status).toBe(200);
    expect(s.ado.item(10).state).toBe("Active");
    const invalid = await call(s, "PATCH", `${site(s)}/_apis/wit/workitems/10?api-version=7.1`, a, [{ op: "add", path: "/fields/System.State", value: "Shipped" }], "application/json-patch+json");
    expect(invalid.status).toBe(400);
    expect(invalid.json.message).toMatch(/TF401320/);
    expect(s.transitions).toMatchObject([{ tracker: "ado", key: "10", from: "New", to: "Active" }]);
  });

  test("boards: the project's teams; a team's area paths become the intake WIQL condition; a gone team 404s", async ({ fakeTracker: s }) => {
    seed(s);
    const a = s.ado.authorization;
    s.ado.update(10, { areaPath: "Shop\\Web\\Checkout" });
    s.ado.update(11, { areaPath: "Shop\\Shared" });
    const web = s.ado.addTeam({ name: "Web", values: [{ value: "Shop\\Web", includeChildren: true }, { value: "Shop\\Shared", includeChildren: false }] });

    // The runner's Board select: GET {org}/_apis/projects/{project}/teams.
    const teams = await call(s, "GET", `${site(s)}/_apis/projects/Shop/teams?$top=500&api-version=7.1`, a);
    expect(teams.json.value.map((t: { name: string }) => t.name).sort()).toEqual(["Shop Team", "Web"]);
    expect(teams.json.value.find((t: { name: string }) => t.name === "Web").id).toBe(web.id);

    // Each poll: the team's field values, by id (or name).
    const values = await call(s, "GET", `${site(s)}/Shop/${web.id}/_apis/work/teamsettings/teamfieldvalues?api-version=7.1`, a);
    expect(values.json).toMatchObject({
      field: { referenceName: "System.AreaPath" },
      defaultValue: "Shop\\Web",
      values: [{ value: "Shop\\Web", includeChildren: true }, { value: "Shop\\Shared", includeChildren: false }],
    });
    expect((await call(s, "GET", `${site(s)}/Shop/Web/_apis/work/teamsettings/teamfieldvalues?api-version=7.1`, a)).status).toBe(200);

    // Work items carry their area path; the board's condition (AdoQueries.TeamFieldCondition) filters intake.
    const batch = await call(s, "POST", `${site(s)}/_apis/wit/workitemsbatch?api-version=7.1`, a, { ids: [10], $expand: "Relations", errorPolicy: "Omit" });
    expect(batch.json.value[0].fields["System.AreaPath"]).toBe("Shop\\Web\\Checkout");
    const wiql = (query: string) => call(s, "POST", `${site(s)}/Shop/_apis/wit/wiql?api-version=7.1`, a, { query });
    const board = "([System.AreaPath] UNDER 'Shop\\Web' OR [System.AreaPath] = 'Shop\\Shared')";
    const onBoard = await wiql(`SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.Id] IN (10, 11, 12, 13) AND ${board} ORDER BY [System.Id]`);
    expect(onBoard.json.workItems.map((w: { id: number }) => w.id)).toEqual([10, 11]);
    const exact = await wiql("SELECT [System.Id] FROM WorkItems WHERE [System.AreaPath] = 'Shop\\Web' ORDER BY [System.Id]");
    expect(exact.json.workItems).toEqual([]);

    s.ado.removeTeam(web.id);
    const gone = await call(s, "GET", `${site(s)}/Shop/${web.id}/_apis/work/teamsettings/teamfieldvalues?api-version=7.1`, a);
    expect(gone.status).toBe(404);
    expect(gone.json.message).toMatch(/TF400299/);
    expect((await call(s, "GET", `${site(s)}/_apis/projects/Nope/teams?$top=500&api-version=7.1`, a)).status).toBe(404);
  });
});
