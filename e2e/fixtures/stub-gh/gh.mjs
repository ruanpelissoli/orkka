#!/usr/bin/env node
// Stub GitHub CLI for the Orkka e2e harness. It answers exactly the `gh` invocations the
// runner's ForgeCli makes (orkka-api/src/Orkka.LocalRunner/ForgeCli.cs) the way the real CLI
// prints them, records every call, and keeps pull requests in a JSON state file — so a
// "publish to forge" can be asserted without GitHub.
//
//   gh auth status [--hostname H]                      exit 0 + "Logged in to H account A (keyring)" on stderr
//   gh auth token --hostname H --user A                prints a fake token for A (ForgeCli scopes GH_TOKEN with it)
//   gh pr list --repo R --head B --state open --json url --jq .[0].url
//   gh pr create --repo R --head B --base M --title T --body-file F [--draft]   prints the new PR URL
//   gh pr view URL --json state --jq .state            OPEN | MERGED | CLOSED
//   gh pr ready URL --repo R [--undo]                  draft → ready (or back to draft)
//   gh pr edit URL --repo R --body-file F              replaces the body (a Story's checklist)
//   gh pr merge URL                                    (test convenience) marks it MERGED
//
// State lives next to this file (state.json, calls.jsonl) unless GH_STUB_DIR says otherwise.
// Nothing here talks to the network.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const dir = process.env.GH_STUB_DIR || path.dirname(fileURLToPath(import.meta.url));
const statePath = path.join(dir, "state.json");
const callsPath = path.join(dir, "calls.jsonl");

const DEFAULT_STATE = {
  signedIn: true,
  host: "github.com",
  accounts: ["e2e-dev"],
  activeAccount: "e2e-dev",
  nextNumber: 1,
  prs: [],
  // When set, `pr create` fails with this message on stderr (exit 1).
  failCreate: null,
};

function readState() {
  try {
    return { ...DEFAULT_STATE, ...JSON.parse(fs.readFileSync(statePath, "utf8")) };
  } catch {
    return { ...DEFAULT_STATE };
  }
}

function writeState(state) {
  const tmp = `${statePath}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(state, null, 2));
  fs.renameSync(tmp, statePath);
}

/** Splits argv into positionals and `--flag value` pairs (boolean flags get "true"). */
function parse(argv) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) {
      const eq = a.indexOf("=");
      if (eq > 0) flags[a.slice(2, eq)] = a.slice(eq + 1);
      else if (i + 1 < argv.length && !argv[i + 1].startsWith("--")) flags[a.slice(2)] = argv[++i];
      else flags[a.slice(2)] = "true";
    } else positional.push(a);
  }
  return { positional, flags };
}

const argv = process.argv.slice(2);
const { positional, flags } = parse(argv);
const ghToken = process.env.GH_TOKEN;
fs.mkdirSync(dir, { recursive: true });
fs.appendFileSync(
  callsPath,
  JSON.stringify({
    at: new Date().toISOString(),
    argv,
    cwd: process.cwd(),
    ghToken: ghToken ?? null,
    promptDisabled: process.env.GH_PROMPT_DISABLED === "1",
  }) + "\n",
);

const out = (s) => process.stdout.write(s.endsWith("\n") ? s : s + "\n");
const fail = (msg, code = 1) => {
  process.stderr.write(msg.endsWith("\n") ? msg : msg + "\n");
  process.exit(code);
};

/** The tiny subset of jq the runner uses: `.state`, `.[0].url`, `.[].url`. */
function jq(value, expr) {
  if (!expr) return JSON.stringify(value);
  const m = /^\.\[(\d+)\]\.(\w+)$/.exec(expr);
  if (m) {
    const item = Array.isArray(value) ? value[Number(m[1])] : undefined;
    return item ? String(item[m[2]] ?? "") : "";
  }
  const all = /^\.\[\]\.(\w+)$/.exec(expr);
  if (all) return (Array.isArray(value) ? value : []).map((v) => String(v[all[1]] ?? "")).join("\n");
  const field = /^\.(\w+)$/.exec(expr);
  if (field) return value && typeof value === "object" ? String(value[field[1]] ?? "") : "";
  fail(`gh stub: unsupported --jq expression ${expr}`);
}

function pick(pr, fieldsCsv) {
  const fields = (fieldsCsv || "url,state,number,title").split(",");
  return Object.fromEntries(fields.map((f) => [f, f === "isDraft" ? Boolean(pr.isDraft) : pr[f === "headRefName" ? "head" : f === "baseRefName" ? "base" : f]]));
}

function readBody(file) {
  try {
    return fs.readFileSync(file, "utf8");
  } catch (e) {
    fail(`gh stub: cannot read --body-file: ${e.message}`);
  }
}

const state = readState();
const [group, verb, arg] = positional;

if (group === "--version" || flags.version) {
  out("gh version 2.99.0-stub (orkka-e2e)");
  process.exit(0);
}

if (group === "auth" && verb === "status") {
  if (!state.signedIn) fail("You are not logged into any GitHub hosts. To log in, run: gh auth login");
  const host = flags.hostname || state.host;
  const lines = [host, ...state.accounts.map((a) => `  ✓ Logged in to ${host} account ${a} (keyring)\n  - Active account: ${a === state.activeAccount}`)];
  process.stderr.write(lines.join("\n") + "\n");
  process.exit(0);
}

if (group === "auth" && verb === "token") {
  if (!state.signedIn) fail("no oauth token found for github.com");
  const user = flags.user || state.activeAccount;
  if (!state.accounts.includes(user)) fail(`no oauth token found for ${flags.hostname || state.host} account ${user}`);
  out(`gho_stub_${user}`);
  process.exit(0);
}

if (group === "pr") {
  if (!state.signedIn) fail("To get started with GitHub CLI, please run:  gh auth login", 4);
  if (verb === "list") {
    const repo = flags.repo;
    const wanted = (flags.state || "open").toUpperCase();
    const list = state.prs
      .filter((p) => (!repo || p.repo === repo) && (!flags.head || p.head === flags.head) && (wanted === "ALL" || p.state === wanted))
      .map((p) => pick(p, flags.json));
    out(flags.jq ? jq(list, flags.jq) : JSON.stringify(list));
    process.exit(0);
  }
  if (verb === "create") {
    if (state.failCreate) fail(state.failCreate);
    for (const required of ["repo", "head", "base", "title"]) if (!flags[required]) fail(`gh stub: pr create needs --${required}`);
    const existing = state.prs.find((p) => p.repo === flags.repo && p.head === flags.head && p.state === "OPEN");
    if (existing) fail(`a pull request for branch "${flags.head}" into branch "${existing.base}" already exists:\n${existing.url}`);
    let body = flags.body ?? "";
    if (flags["body-file"]) body = readBody(flags["body-file"]);
    const number = state.nextNumber++;
    const pr = {
      number,
      url: `https://${state.host}/${flags.repo}/pull/${number}`,
      repo: flags.repo,
      head: flags.head,
      base: flags.base,
      title: flags.title,
      body,
      state: "OPEN",
      isDraft: flags.draft === "true",
      history: [flags.draft === "true" ? "created-draft" : "created"],
      author: ghToken?.startsWith("gho_stub_") ? ghToken.slice("gho_stub_".length) : state.activeAccount,
      createdAt: new Date().toISOString(),
    };
    state.prs.push(pr);
    writeState(state);
    // Real gh prints progress to stderr and the URL as the last stdout line.
    process.stderr.write(`\nCreating pull request for ${pr.head} into ${pr.base} in ${pr.repo}\n\n`);
    out(pr.url);
    process.exit(0);
  }
  if (verb === "view" || verb === "merge" || verb === "close" || verb === "ready" || verb === "edit") {
    const target = arg;
    const pr = state.prs.find((p) => p.url === target || String(p.number) === target);
    if (!pr) fail(`GraphQL: Could not resolve to a PullRequest with the number of ${target}. (repository.pullRequest)`);
    if (flags.repo && pr.repo !== flags.repo) fail(`GraphQL: Could not resolve to a PullRequest with the number of ${pr.number}. (repository.pullRequest)`);
    if (verb === "ready") {
      if (pr.state !== "OPEN") fail(`Pull request #${pr.number} is closed. Only draft pull requests can be marked as "ready for review"`);
      const undo = flags.undo === "true";
      if (!undo && !pr.isDraft) {
        process.stderr.write(`! Pull request #${pr.number} is already "ready for review"\n`);
        process.exit(0);
      }
      pr.isDraft = undo;
      (pr.history ??= []).push(undo ? "draft" : "ready");
      writeState(state);
      process.stderr.write(undo ? `✓ Pull request #${pr.number} is converted to "draft"\n` : `✓ Pull request #${pr.number} is marked as "ready for review"\n`);
      process.exit(0);
    }
    if (verb === "edit") {
      if (flags["body-file"]) pr.body = readBody(flags["body-file"]);
      else if (flags.body !== undefined) pr.body = flags.body;
      if (flags.title) pr.title = flags.title;
      (pr.history ??= []).push("edited");
      writeState(state);
      out(pr.url);
      process.exit(0);
    }
    if (verb === "merge") {
      pr.state = "MERGED";
      writeState(state);
      out(`✓ Merged pull request #${pr.number}`);
      process.exit(0);
    }
    if (verb === "close") {
      pr.state = "CLOSED";
      writeState(state);
      out(`✓ Closed pull request #${pr.number}`);
      process.exit(0);
    }
    const view = pick(pr, flags.json);
    out(flags.jq ? jq(view, flags.jq) : JSON.stringify(view));
    process.exit(0);
  }
}

fail(`gh stub: unsupported command: gh ${argv.join(" ")}`);
