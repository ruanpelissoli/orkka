import fs from "node:fs";
import path from "node:path";
import { execa } from "execa";
import { test, expect, caseIds } from "../../src/fixtures/test.js";
import { cfg, env, needsEnvironment } from "../../config/index.js";
import { FakeTrackerServer } from "../../src/fakes/tracker/server.js";
import { StubGh } from "../../src/fakes/stub-gh.js";
import { stubBinDir } from "../../src/local/stack.js";
import { databaseContains, filesContaining } from "../../src/local/secret-scan.js";
import { createBaseFolder, type BaseFolder } from "../../src/testdata/base-folder.js";
import {
  addTrackerConnection,
  applyToTask,
  createProjectFromFolder,
  discuss,
  followAnswering,
  followUntil,
  listTasks,
  messagesText,
  pendingQuestion,
  useCheapestModel,
  waitForTicketTask,
  type FolderProject,
} from "../../src/flows/local-workflow.js";
import { recordCreatedProject } from "../../src/safety/sweep.js";

/**
 * The developer's workflow on the local stack, end to end, as one serial story:
 *
 *  1. a base folder `shop` (api/ = a forge-looking remote that is a local bare repo, web/ = no
 *     origin) becomes a project with two repositories;
 *  2. a Jira connection is added through the write-only host → runner op — the token ends up in
 *     the sandbox's file secret store and nowhere else;
 *  3. the ticket source: intake "To Do", status map planning → In Progress, PR opened → Code
 *     Review, merged → Done;
 *  4. a standalone ticket → task: Discuss, plan approval, build, review, publish via the stub gh;
 *     write-back In Progress → Code Review; a Jira comment reaches the thread;
 *  5. a ticket that names no repository → the repository question → answered through Discuss
 *     "Apply"; moving it to In Progress in Jira does not retire it;
 *  6. a Story with two sub-tasks: one branch, a draft PR after the first, the second builds on the
 *     first and shows only its own change, ready at the end, Story + sub-tasks → Code Review;
 *  7. Azure DevOps: connection + intake.
 *
 * Steps 4–6 run real agents on the developer's own AI subscription and are skipped unless
 * `E2E_WORKFLOW_AGENTS=1` (they cost three small pipeline runs on Haiku). @workflow
 */
const AGENTS = process.env.E2E_WORKFLOW_AGENTS === "1";
const JIRA_STATUSES = [
  { name: "To Do", category: "new" as const },
  { name: "In Progress", category: "indeterminate" as const },
  { name: "Code Review", category: "indeterminate" as const },
  { name: "Done", category: "done" as const },
];

test.describe("the developer's workflow on the local stack @workflow", () => {
  test.skip(Boolean(needsEnvironment()), needsEnvironment() ?? "");
  test.describe.configure({ mode: "serial" });
  test.setTimeout(60 * 60_000);

  let fake: FakeTrackerServer;
  let gh: StubGh;
  let folder: BaseFolder;
  let created: FolderProject;
  let connectionId = "";
  const pid = () => created.project.id;
  const apiRepo = () => created.repos.find((r) => r.folderPath === "api")!;

  test.beforeAll(async () => {
    fake = await new FakeTrackerServer({ jira: { statuses: JIRA_STATUSES } }).start();
    gh = await StubGh.install(stubBinDir());
    folder = await createBaseFolder(path.join(env.E2E_SCRATCH_DIR, "folders", `${Date.now().toString(36)}`), "shop");
  });
  test.afterAll(async () => {
    await fake?.stop();
  });

  test("1. a base folder becomes a project: api is a remote, web a local folder", caseIds("LWF-002"), async ({ api, page }) => {
    created = await createProjectFromFolder(api, folder.root, `${cfg.projectPrefix}shop-${Date.now().toString(36)}`);
    recordCreatedProject({ id: created.project.id, name: created.project.name }, test.info().title);
    expect(created.project.baseFolderShape).toBe("multirepo");
    expect(created.repos.map((r) => [r.folderPath, r.location]).sort()).toEqual([["api", "remote"], ["web", "local"]]);
    expect(apiRepo().gitUrl, "the configured origin, not its insteadOf rewrite").toBe(folder.api.remote.aliasUrl);
    expect(created.repos.every((r) => r.workspaceStatus !== "error"), JSON.stringify(created.repos)).toBe(true);
    const repos = await api.repos.list(pid());
    expect(repos.find((r) => r.name === "api")?.provider).toBe("github");
    expect(repos.find((r) => r.name === "web")?.provider).toBe("local");
    await useCheapestModel(api, pid());

    await page.goto(`/en/p/${created.project.slug}/repos`);
    await expect(page.getByText("api", { exact: true }).first()).toBeVisible();
    await expect(page.getByText(/github\.com/).first()).toBeVisible();
    await expect(page.getByText(/local folder/i).first()).toBeVisible();
  });

  test("2. a Jira connection added from Settings → Trackers: the token lives only in the runner's secret store", caseIds("LWF-003"), async ({ api }) => {
    const r = await addTrackerConnection(api, { kind: "jira", site: fake.jiraSiteUrl, email: fake.jira.email, token: fake.jira.token });
    expect(r, JSON.stringify(r)).toMatchObject({ supported: true, ok: true });
    connectionId = r.connection!.id;
    expect(JSON.stringify(r)).not.toContain(fake.jira.token);

    const hits = filesContaining(fake.jira.token);
    expect(hits.map((h) => path.relative(path.join(env.E2E_LOCAL_SANDBOX, "home"), h).replace(/\\/g, "/"))).toEqual([
      `workspaces/${(await api.org.me()).orgName.toLowerCase()}/secrets/${r.connection!.key}`,
    ]);
    expect(databaseContains(fake.jira.token), "token in the SQLite database").toBe(false);
    // trackers.json sits next to the secret and must hold metadata only (covered by the scan above).
  });

  test("3. the ticket source: To Do intake and the six-step status map", caseIds("LWF-004", "TSRC-004"), async ({ api }) => {
    const statuses = await api.trackers.query(connectionId, { kind: "statuses", scope: "SHOP" } as never);
    expect(JSON.stringify(statuses)).toContain("Code Review");
    const saved = await api.projects.tracker.put(pid(), {
      trackerConnectionId: connectionId,
      scope: "SHOP",
      scopeMode: "All",
      intakeStatuses: ["To Do"],
      autoSubmit: true,
      pollIntervalSeconds: 60,
      statusMap: { pickedUp: null, planning: "In Progress", developing: null, inReview: null, prOpened: "Code Review", merged: "Done" },
    } as never);
    expect(saved).toMatchObject({ intakeStatuses: ["To Do"], statusMap: { planning: "In Progress", inReview: null, prOpened: "Code Review", merged: "Done" } });
  });

  test("4. a standalone ticket: Discuss, plan, build, review, publish; Jira follows", caseIds("LWF-005", "FRG-001", "TING-006", "TWB-001", "TWB-002"), async ({ api }) => {
    test.skip(!AGENTS, "real agent runs — set E2E_WORKFLOW_AGENTS=1");
    fake.jira.addIssue({ key: "SHOP-11", summary: "Add an add(a, b) function to the api service math module", descriptionHtml: "<p>In the <b>api</b> service add <code>export function add(a, b)</code> to <code>src/math.js</code> next to <code>sub</code>, with a unit test. Nothing else.</p>" });
    let task = await waitForTicketTask(api, pid(), "SHOP-11");
    expect(task.repoId, "routed to api").toBe(apiRepo().id);
    task = await followUntil(api, task.id, (t) => ["PlanReady", "PendingQuestions"].includes(String(t.currentLane)), "SHOP-11 planned");
    if (task.currentLane === "PendingQuestions") {
      const q = await pendingQuestion(api, task.id);
      await applyToTask(api, task.id, { text: q?.options?.[0] ?? "Keep it minimal." });
      task = await followUntil(api, task.id, (t) => t.currentLane === "PlanReady", "SHOP-11 planned after the answer");
    }
    const reply = await discuss(api, task.id, "Will the plan change any file besides src/math.js and test/math.test.js? One sentence.");
    expect(reply.length).toBeGreaterThan(0);
    fake.jira.addComment("SHOP-11", "<p>PO here: keep the function pure.</p>", "Pat Owner");
    const plan = await api.plans.latest(task.id);
    await api.plans.review(task.id, plan!.id, "approve");
    task = await followUntil(api, task.id, (t) => t.status === "WaitingCodeReview" || t.currentLane === "Done", "SHOP-11 reviewed");
    if (task.currentLane !== "Done") await api.pr.merge(task.id);
    task = await followUntil(api, task.id, (t) => Boolean(t.publishedPrUrl), "SHOP-11 published");
    const pr = gh.state().prs.find((p) => p.head === task.branchName);
    expect(pr, "PR created through the stub gh").toBeTruthy();
    expect(task.publishedPrUrl).toBe(pr!.url);
    // FRG-001: one `gh pr create` for the task branch against the repo's default branch.
    const creates = gh.callsOf("pr", "create").filter((c) => c.argv.includes(task.branchName!));
    expect(creates, "exactly one gh pr create for the task branch").toHaveLength(1);
    expect(creates[0].argv[creates[0].argv.indexOf("--base") + 1]).toBe("main");
    // TWB-001/002: Planning → In Progress, PR opened → Code Review, each acknowledged on the thread.
    await expect.poll(() => fake.jira.transitions.filter((t) => t.key === "SHOP-11").map((t) => t.to), { timeout: 6 * 60_000, intervals: [10_000] }).toEqual(["In Progress", "Code Review"]);
    await expect
      .poll(async () => (await messagesText(api, task.id)).filter((m) => /^Moved SHOP-11 to “.+” in Jira\.$/.test(m.content)).map((m) => m.content), { timeout: 2 * 60_000, intervals: [5_000] })
      .toEqual(["Moved SHOP-11 to “In Progress” in Jira.", "Moved SHOP-11 to “Code Review” in Jira."]);
    // TING-006: the Jira comment joins the thread as an ExternalComment.
    const thread = await messagesText(api, task.id);
    expect(thread.some((m) => m.messageType === "ExternalComment" && m.content.includes("keep the function pure"))).toBe(true);
    expect(thread.filter((m) => /was updated in Jira/.test(m.content)), "a status move is not an edit").toEqual([]);
  });

  test("5. a ticket that names no repository: the question routes it; moving it in Jira does not retire it", caseIds("LWF-006"), async ({ api }) => {
    test.skip(!AGENTS, "real agent runs — set E2E_WORKFLOW_AGENTS=1");
    fake.jira.addIssue({ key: "SHOP-12", summary: "Add a mul(a, b) helper to math.js", descriptionHtml: "<p>Add <code>export function mul(a, b)</code> to <code>src/math.js</code>, with a unit test.</p>" });
    let task = await waitForTicketTask(api, pid(), "SHOP-12");
    fake.jira.update("SHOP-12", { status: "In Progress" });
    if (!task.repoId) {
      task = await followUntil(api, task.id, (t) => t.currentLane === "PendingQuestions", "the repository question");
      const q = await pendingQuestion(api, task.id);
      expect(q?.options.sort()).toEqual(["api", "web"]);
      const applied = await applyToTask(api, task.id, { text: "api" });
      expect(applied.action).toBe("answered");
    }
    task = await followUntil(api, task.id, (t) => Boolean(t.repoId) && t.currentLane !== "PendingQuestions", "SHOP-12 routed", 10 * 60_000);
    expect(task.repoId).toBe(apiRepo().id);
    // Two polls later it is still a live task.
    await new Promise((r) => setTimeout(r, 130_000));
    expect((await api.tasks.get(task.id)).isArchived).toBe(false);
  });

  test("6. a Story with two sub-tasks: one branch, draft PR, the second builds on the first, ready at the end", caseIds("LWF-007"), async ({ api }) => {
    test.skip(!AGENTS, "real agent runs — set E2E_WORKFLOW_AGENTS=1");
    const p = await api.projects.get(pid());
    await api.projects.patch(pid(), { settings: { ...p.settings, autoApprovePlan: true, autoApproveCodeReview: true } as never });
    fake.jira.addIssue({ key: "SHOP-20", type: "Story", summary: "Math helpers for the api service", descriptionHtml: "<p>Two helpers in the <b>api</b> service's src/math.js, one per sub-task.</p>" });
    fake.jira.addIssue({ key: "SHOP-21", type: "Sub-task", parent: "SHOP-20", assignee: "me", status: "To Do", summary: "Add div(a, b) to the api service math module", descriptionHtml: "<p>In the api service add <code>export function div(a, b)</code> to src/math.js with a unit test.</p>" });
    fake.jira.addIssue({ key: "SHOP-22", type: "Sub-task", parent: "SHOP-20", assignee: "me", status: "To Do", summary: "Add neg(a) to the api service math module", descriptionHtml: "<p>In the api service add <code>export function neg(a)</code> to src/math.js with a unit test.</p>" });
    const first = await waitForTicketTask(api, pid(), "SHOP-21");
    const second = await waitForTicketTask(api, pid(), "SHOP-22");
    expect(first.groupId).toBeTruthy();
    expect(second.groupId).toBe(first.groupId);
    expect([first.storyOrder, second.storyOrder]).toEqual([1, 2]);
    expect(second.branchName).toBe(first.branchName);

    await followAnswering(api, first.id, (t) => t.currentLane === "Done" && Boolean(t.isMerged), "the first sub-task landed", 40 * 60_000);
    await expect.poll(() => gh.state().prs.find((x) => x.head === first.branchName)?.isDraft, { timeout: 5 * 60_000 }).toBe(true);

    await followAnswering(api, second.id, (t) => t.currentLane === "Done" && Boolean(t.isMerged), "the second sub-task landed", 40 * 60_000);
    const branch = first.branchName!;
    const log = (await execa("git", ["--git-dir", folder.api.remote.dir, "log", "--format=%s", `${branch}`])).stdout;
    expect(log).toMatch(/SHOP-21/);
    expect(log).toMatch(/SHOP-22/);
    // The second member's review shows its own change only.
    const snaps = await api.tasks.codeChanges(second.id);
    const diff = await api.tasks.codeChangeDiff(second.id, snaps.at(-1)!.id);
    // Only ADDED lines count: the first member's div() may sit next to neg() as unchanged
    // context (` export function div…`), which is correct (R5 run).
    const added = String((diff as { diff?: string }).diff ?? "").split("\n").filter((l) => l.startsWith("+") && !l.startsWith("+++"));
    expect(added.join("\n")).toContain("neg");
    expect(added.filter((l) => /export function div/.test(l)), "the first member's change shows in the second's review").toEqual([]);

    await expect.poll(() => gh.state().prs.find((x) => x.head === branch)?.isDraft, { timeout: 5 * 60_000 }).toBe(false);
    await expect
      .poll(() => ["SHOP-20", "SHOP-21", "SHOP-22"].map((k) => fake.jira.issue(k).status), { timeout: 8 * 60_000, intervals: [15_000] })
      .toEqual(["Code Review", "Code Review", "Code Review"]);
  });

  test("7. Azure DevOps: a PAT connection and a work item in intake", caseIds("LWF-008"), async ({ api }) => {
    const r = await addTrackerConnection(api, { kind: "ado", site: fake.adoSiteUrl, token: fake.ado.pat });
    expect(r, JSON.stringify(r)).toMatchObject({ supported: true, ok: true });
    expect(databaseContains(fake.ado.pat)).toBe(false);
    const project = fake.ado.projects[0];
    fake.ado.add({ id: 501, type: "Task", title: "Add max(a, b) to the api service math module", state: "New", assignedToMe: true, project, descriptionHtml: "<div>In the api service add max(a, b) to src/math.js.</div>" });
    // A second project on the same base folder, fed by ADO, hands-off (no agent runs).
    const second = await createProjectFromFolder(api, folder.root, `${cfg.projectPrefix}shop-ado-${Date.now().toString(36)}`);
    recordCreatedProject({ id: second.project.id, name: second.project.name }, test.info().title);
    await api.projects.tracker.put(second.project.id, {
      trackerConnectionId: r.connection!.id,
      scope: project,
      scopeMode: "All",
      intakeStatuses: ["New"],
      autoSubmit: false,
      pollIntervalSeconds: 60,
      statusMap: { planning: "Active", prOpened: "Resolved", merged: "Closed" },
    } as never);
    const task = await waitForTicketTask(api, second.project.id, "501");
    expect(task.title).toContain("max(a, b)");
    expect((await listTasks(api, second.project.id)).length).toBe(1);
  });
});

void fs;
