import { test, expect } from "../../src/fixtures/test.js";
import type { FakeTrackerServer } from "../../src/fakes/tracker/server.js";

/**
 * Self-tests of the fake GitHub GraphQL API: each query below is the one the runner's
 * GitHubProjectsClient sends (same operation text, variables and Bearer auth), and each assertion
 * is on a field the client parses. No Orkka deployment needed. @offline
 */

async function gql(s: FakeTrackerServer, query: string, variables: Record<string, unknown> = {}, auth: string | null = s.github.authorization) {
  const res = await fetch(`${s.githubSiteUrl}/graphql`, {
    method: "POST",
    headers: { Accept: "application/json", "User-Agent": "orkka", "Content-Type": "application/json", ...(auth ? { Authorization: auth } : {}) },
    body: JSON.stringify({ query, variables }),
  });
  const text = await res.text();
  return { status: res.status, headers: res.headers, json: text ? JSON.parse(text) : undefined };
}

const ISSUE_FIELDS = `fragment IssueFields on Issue { id number title url body state updatedAt repository { nameWithOwner url } assignees(first: 20) { nodes { login } } labels(first: 30) { nodes { name } } issueType { name } parent { number repository { nameWithOwner } issueType { name } } subIssues(first: 100) { nodes { number repository { nameWithOwner } subIssuesSummary { total } } } blockedBy(first: 50) { nodes { number repository { nameWithOwner } } } }`;

const seed = (s: FakeTrackerServer) => {
  const g = s.github;
  const p = g.addProject({ title: "Shop roadmap" });
  g.addProject({ title: "Old board", closed: true });
  g.addIssue({ repo: "e2e-local/app", number: 1, title: "Mine, Todo", body: "Do it.\n<!-- template -->", labels: ["bug"] });
  g.addIssue({ repo: "e2e-local/app", number: 2, title: "Colleague's", assignees: ["colleague"] });
  g.addIssue({ repo: "e2e-local/api", number: 3, title: "Other repo" });
  g.addIssue({ repo: "e2e-local/app", number: 4, title: "Story", issueType: "Feature", subIssues: ["e2e-local/app#5", "e2e-local/app#6"], blockedBy: ["e2e-local/api#3"] });
  g.addIssue({ repo: "e2e-local/app", number: 5, title: "Sub mine" });
  g.addIssue({ repo: "e2e-local/app", number: 6, title: "Sub colleague", assignees: ["colleague"] });
  for (const [key, status] of [["e2e-local/app#1", "Todo"], ["e2e-local/app#2", "Todo"], ["e2e-local/api#3", "Todo"], ["e2e-local/app#4", "Todo"], ["e2e-local/app#5", "Todo"]] as const)
    g.addItem(p.id, { issueKey: key, status });
  g.addItem(p.id, { type: "DRAFT_ISSUE", status: "Todo" });
  g.addItem(p.id, { type: "PULL_REQUEST", status: "Todo" });
  return p;
};

test.describe("fake GitHub GraphQL @offline", () => {
  test("auth and scopes: viewer for the Bearer token with X-OAuth-Scopes; anything else 401", async ({ fakeTracker: s }) => {
    const ok = await gql(s, "query { viewer { login name } }");
    expect(ok.status).toBe(200);
    expect(ok.json.data.viewer).toEqual({ login: s.github.login, name: s.github.name });
    expect(ok.headers.get("x-oauth-scopes")).toMatch(/project/);
    s.github.scopes = "repo, read:org";
    expect((await gql(s, "query { viewer { login name } }")).headers.get("x-oauth-scopes")).toBe("repo, read:org");
    const bad = await gql(s, "query { viewer { login name } }", {}, "Bearer nope");
    expect(bad.status).toBe(401);
    expect(bad.json.message).toBe("Bad credentials");
    expect(s.requestsTo("github").map((r) => r.authOk)).toEqual([true, true, false]);
  });

  test("owners, an owner's open and closed projects, a project's single-select fields", async ({ fakeTracker: s }) => {
    const p = seed(s);
    const owners = await gql(s, "query($after: String) { viewer { login name organizations(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { login name } } } }", { after: null });
    expect(owners.json.data.viewer.organizations.nodes).toEqual([{ login: "e2e-local", name: "E2E Local" }]);
    const boards = await gql(s, "query($login: String!, $after: String) { repositoryOwner(login: $login) { ... on ProjectV2Owner { projectsV2(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { id number title closed url } } } } }", { login: "e2e-local", after: null });
    expect(boards.json.data.repositoryOwner.projectsV2.nodes.map((n: { title: string; closed: boolean }) => `${n.title}:${n.closed}`)).toEqual(["Shop roadmap:false", "Old board:true"]);
    const gone = await gql(s, "query($login: String!, $after: String) { repositoryOwner(login: $login) { ... on ProjectV2Owner { projectsV2(first: 100, after: $after) { nodes { id } } } } }", { login: "nobody" });
    expect(gone.json.data.repositoryOwner).toBeNull();
    const project = await gql(s, "query($id: ID!) { node(id: $id) { ... on ProjectV2 { id title closed fields(first: 50) { nodes { ... on ProjectV2SingleSelectField { id name options { id name } } } } } } }", { id: p.id });
    const fields = project.json.data.node.fields.nodes.filter((f: { name?: string }) => f.name);
    expect(fields.map((f: { name: string }) => f.name)).toEqual(["Status", "Priority"]);
    expect(fields[0].options.map((o: { name: string }) => o.name)).toEqual(["Todo", "In Progress", "In Review", "Done"]);
    const missing = await gql(s, "query($id: ID!) { node(id: $id) { ... on ProjectV2 { id title closed fields(first: 50) { nodes { id } } } } }", { id: "PVT_gone" });
    expect(missing.json.data.node).toBeNull();
    expect(missing.json.errors[0].type).toBe("NOT_FOUND");
  });

  test("items: paged, every type with its status; drafts and pull requests have no Issue content", async ({ fakeTracker: s }) => {
    const p = seed(s);
    const q = `query($id: ID!, $after: String, $field: String!) { viewer { login } node(id: $id) { ... on ProjectV2 { items(first: 4, after: $after) { pageInfo { hasNextPage endCursor } nodes { id type isArchived fieldValueByName(name: $field) { ... on ProjectV2ItemFieldSingleSelectValue { name } } content { ... on Issue { ...IssueFields } } } } } } } ${ISSUE_FIELDS}`;
    const first = await gql(s, q, { id: p.id, after: null, field: "Status" });
    expect(first.json.data.viewer.login).toBe(s.github.login);
    expect(first.json.data.node.items.pageInfo.hasNextPage).toBe(true);
    const second = await gql(s, q, { id: p.id, after: first.json.data.node.items.pageInfo.endCursor, field: "Status" });
    expect(second.json.data.node.items.pageInfo.hasNextPage).toBe(false);
    const nodes = [...first.json.data.node.items.nodes, ...second.json.data.node.items.nodes];
    expect(nodes.map((n: { type: string }) => n.type)).toEqual(["ISSUE", "ISSUE", "ISSUE", "ISSUE", "ISSUE", "DRAFT_ISSUE", "PULL_REQUEST"]);
    expect(nodes[5].content).toEqual({});
    expect(nodes[0].fieldValueByName).toEqual({ name: "Todo", optionId: expect.any(String) });
    expect(nodes[0].content).toMatchObject({ number: 1, body: "Do it.\n<!-- template -->", repository: { nameWithOwner: "e2e-local/app" }, assignees: { nodes: [{ login: s.github.login }] }, labels: { nodes: [{ name: "bug" }] } });
    // Each sub-issue's own count is what the runner's depth rule reads; the parent's type is the hint.
    expect(nodes[3].content.subIssues.nodes).toEqual([
      { number: 5, repository: { nameWithOwner: "e2e-local/app" }, subIssuesSummary: { total: 0 } },
      { number: 6, repository: { nameWithOwner: "e2e-local/app" }, subIssuesSummary: { total: 0 } },
    ]);
    expect(nodes[3].content.blockedBy.nodes).toEqual([{ number: 3, repository: { nameWithOwner: "e2e-local/api" } }]);
    expect(nodes[4].content.parent).toEqual({ number: 4, repository: { nameWithOwner: "e2e-local/app" }, issueType: { name: "Feature" } });
  });

  test("issues by key: aliased batch; unknown repository or number → null + NOT_FOUND; status on the project", async ({ fakeTracker: s }) => {
    const p = seed(s);
    const item = (alias: string, owner: string, repo: string, n: number) =>
      `  ${alias}: repository(owner: "${owner}", name: "${repo}") { issue(number: ${n}) { ...IssueFields projectItems(first: 20, includeArchived: false) { nodes { id project { id } fieldValueByName(name: $field) { ... on ProjectV2ItemFieldSingleSelectValue { name } } } } } }\n`;
    const q = `query($field: String!) {\n  viewer { login }\n${item("i0", "e2e-local", "app", 6)}${item("i1", "e2e-local", "app", 99)}${item("i2", "e2e-local", "gone", 1)}}\n${ISSUE_FIELDS}`;
    const r = await gql(s, q, { field: "Status" });
    expect(r.json.data.i0.issue).toMatchObject({ number: 6, assignees: { nodes: [{ login: "colleague" }] }, projectItems: { nodes: [] } });
    expect(r.json.data.i1).toEqual({ issue: null });
    expect(r.json.data.i2).toBeNull();
    expect(r.json.errors.map((e: { type: string }) => e.type)).toEqual(["NOT_FOUND", "NOT_FOUND"]);
    const r2 = await gql(s, `query($field: String!) {\n  viewer { login }\n${item("i0", "e2e-local", "app", 1)}}\n${ISSUE_FIELDS}`, { field: "Status" });
    expect(r2.json.data.i0.issue.projectItems.nodes).toEqual([{ id: expect.any(String), project: { id: p.id }, fieldValueByName: { name: "Todo", optionId: expect.any(String) } }]);
    expect(r2.json.errors).toBeUndefined();
  });

  test("comments newest last; write-back looks the item up, adds a missing one, sets the Status option", async ({ fakeTracker: s }) => {
    const p = seed(s);
    s.github.addComment("e2e-local/app#1", "First", "colleague");
    s.github.addComment("e2e-local/app#1", "Second", "lead");
    const issue = s.github.issues.get("e2e-local/app#1")!;
    const comments = await gql(s, "query($id: ID!, $n: Int!) { node(id: $id) { ... on Issue { comments(last: $n) { nodes { id author { login } createdAt body } } } } }", { id: issue.id, n: 30 });
    expect(comments.json.data.node.comments.nodes.map((c: { body: string; author: { login: string } }) => `${c.author.login}:${c.body}`)).toEqual(["colleague:First", "lead:Second"]);

    const lookup = "query($id: ID!, $field: String!) { node(id: $id) { ... on Issue { projectItems(first: 50, includeArchived: true) { nodes { id project { id } fieldValueByName(name: $field) { ... on ProjectV2ItemFieldSingleSelectValue { optionId } } } } } } }";
    const items = await gql(s, lookup, { id: issue.id, field: "Status" });
    const itemId = items.json.data.node.projectItems.nodes[0].id;
    const inProgress = p.fields[0].options.find((o) => o.name === "In Progress")!;
    const update = "mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) { updateProjectV2ItemFieldValue(input: { projectId: $project, itemId: $item, fieldId: $field, value: { singleSelectOptionId: $option } }) { projectV2Item { id } } }";
    const moved = await gql(s, update, { project: p.id, item: itemId, field: p.fields[0].id, option: inProgress.id });
    expect(moved.json.data.updateProjectV2ItemFieldValue.projectV2Item.id).toBe(itemId);
    expect(s.github.statusChanges).toMatchObject([{ key: "e2e-local/app#1", from: "Todo", to: "In Progress" }]);

    // A sub-issue not on the project yet is added first.
    const sub = s.github.issues.get("e2e-local/app#6")!;
    expect((await gql(s, lookup, { id: sub.id, field: "Status" })).json.data.node.projectItems.nodes).toEqual([]);
    const added = await gql(s, "mutation($project: ID!, $content: ID!) { addProjectV2ItemById(input: { projectId: $project, contentId: $content }) { item { id } } }", { project: p.id, content: sub.id });
    expect(added.json.data.addProjectV2ItemById.item.id).toMatch(/^PVTI_/);
    expect(s.github.addedItems).toEqual([{ key: "e2e-local/app#6", projectId: p.id }]);
  });

  test("error knobs: SAML SSO FORBIDDEN, a secondary rate limit with Retry-After, an outage", async ({ fakeTracker: s }) => {
    const p = seed(s);
    s.github.ssoOrgs.add("e2e-local");
    const sso = await gql(s, "query($id: ID!) { node(id: $id) { ... on ProjectV2 { id title closed fields(first: 50) { nodes { id } } } } }", { id: p.id });
    expect(sso.json.errors[0]).toMatchObject({ type: "FORBIDDEN", message: expect.stringMatching(/SAML enforcement/) });
    s.github.ssoOrgs.clear();
    s.github.rateLimitNext = 3;
    const limited = await gql(s, "query { viewer { login name } }");
    expect(limited.status).toBe(403);
    expect(limited.headers.get("retry-after")).toBe("3");
    expect((await gql(s, "query { viewer { login name } }")).status).toBe(200);
    s.down = true;
    expect((await gql(s, "query { viewer { login name } }")).status).toBe(503);
  });
});
