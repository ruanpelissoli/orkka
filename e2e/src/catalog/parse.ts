import fs from "node:fs";
import path from "node:path";
import { parse as parseYaml } from "yaml";
import { paths } from "../../config/index.js";

/**
 * `hybrid-repo` a remote repo by URL on the developer's runner · `hybrid-folder` a linked local
 * folder · `tracker` a project fed by a Jira/ADO ticket source (the fake tracker locally) ·
 * `offline` needs no deployed Orkka at all (fake servers, stubs, the runner CLI) · `all`.
 * The managed/hosted, cloud and Orkka Local modes were removed with the 2026-10 pivot.
 */
export const MODES = ["hybrid-repo", "hybrid-folder", "tracker", "offline", "all"] as const;
export const SURFACES = ["ui", "api", "cli", "mobile", "admin", "hub"] as const;
export const AUTOMATION = ["automated", "planned", "manual", "blocked", "retired"] as const;
export const PRIORITIES = ["P0", "P1", "P2", "P3"] as const;

export interface CatalogCase {
  id: string;
  area: string;
  title: string;
  file: string;
  line: number;
  priority: string;
  modes: string[];
  surface: string[];
  automation: string;
  refs: string[];
  blockedBy?: string;
  costUsd?: number;
  since?: string;
  tests: string[];
}

export interface CatalogFile {
  file: string;
  area: string;
  title: string;
  modesDefault: string[];
  cases: CatalogCase[];
}

export interface ParseIssue {
  file: string;
  line: number;
  message: string;
}

const HEADING = /^## ([A-Z][A-Z0-9]{1,5})-(\d{3,4})\s+(.+?)\s*$/;

export function parseCatalogFile(absPath: string): { file: CatalogFile; issues: ParseIssue[] } {
  const rel = path.relative(paths.root, absPath).replace(/\\/g, "/");
  const text = fs.readFileSync(absPath, "utf8");
  const lines = text.split(/\r?\n/);
  const issues: ParseIssue[] = [];

  // Frontmatter
  let fm: Record<string, unknown> = {};
  let i = 0;
  if (lines[0]?.trim() === "---") {
    const end = lines.findIndex((l, idx) => idx > 0 && l.trim() === "---");
    if (end === -1) issues.push({ file: rel, line: 1, message: "unterminated frontmatter" });
    else {
      try {
        fm = (parseYaml(lines.slice(1, end).join("\n")) as Record<string, unknown>) ?? {};
      } catch (e) {
        issues.push({ file: rel, line: 1, message: `frontmatter yaml: ${(e as Error).message}` });
      }
      i = end + 1;
    }
  } else issues.push({ file: rel, line: 1, message: "missing frontmatter" });

  const area = String(fm.area ?? "");
  const modesDefault = Array.isArray(fm["modes-default"]) ? (fm["modes-default"] as string[]) : [];
  const out: CatalogFile = { file: rel, area, title: String(fm.title ?? ""), modesDefault, cases: [] };
  if (!area) issues.push({ file: rel, line: 1, message: "frontmatter.area is required" });

  for (; i < lines.length; i++) {
    const m = HEADING.exec(lines[i]);
    if (!m) {
      if (/^## /.test(lines[i])) issues.push({ file: rel, line: i + 1, message: `heading does not match "## AREA-NNN Title": ${lines[i]}` });
      continue;
    }
    const [, prefix, num, title] = m;
    const id = `${prefix}-${num}`;
    const headingLine = i + 1;
    if (prefix !== area) issues.push({ file: rel, line: headingLine, message: `${id}: prefix "${prefix}" differs from file area "${area}"` });
    if (title.length > 90) issues.push({ file: rel, line: headingLine, message: `${id}: title longer than 90 chars` });

    // Find the yaml block directly after the heading (blank lines allowed).
    let j = i + 1;
    while (j < lines.length && lines[j].trim() === "") j++;
    if (!/^```ya?ml\s*$/.test(lines[j] ?? "")) {
      issues.push({ file: rel, line: headingLine, message: `${id}: missing fenced yaml block after heading` });
      continue;
    }
    const start = j + 1;
    let k = start;
    while (k < lines.length && !/^```\s*$/.test(lines[k])) k++;
    if (k >= lines.length) {
      issues.push({ file: rel, line: start, message: `${id}: unterminated yaml block` });
      break;
    }
    let meta: Record<string, unknown> = {};
    try {
      meta = (parseYaml(lines.slice(start, k).join("\n")) as Record<string, unknown>) ?? {};
    } catch (e) {
      issues.push({ file: rel, line: start, message: `${id}: yaml: ${(e as Error).message}` });
    }
    const c: CatalogCase = {
      id,
      area: prefix,
      title,
      file: rel,
      line: headingLine,
      priority: String(meta.priority ?? ""),
      modes: Array.isArray(meta.modes) ? (meta.modes as string[]) : modesDefault,
      surface: Array.isArray(meta.surface) ? (meta.surface as string[]) : [],
      automation: String(meta.automation ?? ""),
      refs: Array.isArray(meta.refs) ? (meta.refs as unknown[]).map(String) : [],
      blockedBy: meta.blockedBy ? String(meta.blockedBy) : undefined,
      costUsd: typeof meta.costUsd === "number" ? meta.costUsd : undefined,
      since: meta.since ? String(meta.since) : undefined,
      tests: [],
    };
    validateCase(c, issues);
    // Prose sections between the yaml block and the next heading.
    let end = k + 1;
    while (end < lines.length && !/^## /.test(lines[end])) end++;
    const prose = lines.slice(k + 1, end).join("\n");
    for (const section of ["Preconditions", "Steps", "Expected"]) {
      if (!new RegExp(`\\*\\*${section}\\b`).test(prose)) issues.push({ file: rel, line: headingLine, message: `${id}: missing **${section}** section` });
    }
    out.cases.push(c);
    i = end - 1;
  }
  return { file: out, issues };
}

function validateCase(c: CatalogCase, issues: ParseIssue[]) {
  const at = (message: string) => issues.push({ file: c.file, line: c.line, message: `${c.id}: ${message}` });
  if (!(PRIORITIES as readonly string[]).includes(c.priority)) at(`invalid priority "${c.priority}"`);
  if (c.modes.length === 0) at("modes is empty and the file has no modes-default");
  for (const m of c.modes) if (!(MODES as readonly string[]).includes(m)) at(`invalid mode "${m}"`);
  if (c.surface.length === 0) at("surface is required");
  for (const s of c.surface) if (!(SURFACES as readonly string[]).includes(s)) at(`invalid surface "${s}"`);
  if (!(AUTOMATION as readonly string[]).includes(c.automation)) at(`invalid automation "${c.automation}"`);
  if (c.refs.length === 0 && c.automation !== "retired") at("refs must have at least one entry");
  for (const r of c.refs) {
    if (!/^(orkka-api|orkka-ui|i18n|hub|cli|admin-ui|mobile|orkka-infra):/.test(r)) at(`unknown ref form "${r}"`);
  }
  if (c.automation === "blocked" && !c.blockedBy) at("blocked cases need blockedBy");
  if (c.costUsd !== undefined && c.costUsd > 0 && !c.modes.some((m) => m.startsWith("cloud-"))) at("a non-zero costUsd is only allowed on cloud-* cases");
}

export function loadCatalog(): { files: CatalogFile[]; cases: CatalogCase[]; issues: ParseIssue[] } {
  const files: CatalogFile[] = [];
  const issues: ParseIssue[] = [];
  for (const name of fs.readdirSync(paths.catalog).sort()) {
    if (!/^\d{2}-.*\.md$/.test(name)) continue;
    const r = parseCatalogFile(path.join(paths.catalog, name));
    files.push(r.file);
    issues.push(...r.issues);
  }
  const cases = files.flatMap((f) => f.cases);
  const seen = new Map<string, CatalogCase>();
  for (const c of cases) {
    const prev = seen.get(c.id);
    if (prev) issues.push({ file: c.file, line: c.line, message: `${c.id}: duplicate id (also in ${prev.file}:${prev.line})` });
    else seen.set(c.id, c);
  }
  return { files, cases, issues };
}
