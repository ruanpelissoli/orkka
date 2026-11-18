import fs from "node:fs";
import path from "node:path";
import { execaSync } from "execa";
import { env } from "../../config/index.js";

export interface ApiRoute {
  method: string;
  path: string;
  file: string;
  class: string;
  permissions: string[];
  /** `api` = a module endpoint (src/Modules), `local` = the host's own (src/Orkka.Local),
   *  `relay` = host↔runner plumbing (src/Orkka.Runners.Server). All are served by the one process. */
  host: "api" | "local" | "relay";
}
export interface UiPage {
  app: "remote" | "local" | "admin";
  route: string;
  file: string;
}
export interface SurfaceManifest {
  generatedAt: string;
  sources: Record<string, { path: string; sha: string }>;
  uiPages: UiPage[];
  apiRoutes: ApiRoute[];
  hubs: { path: string; hub: string; joinMethods: string[]; events: string[] }[];
  cliVerbs: string[];
  i18n: { namespaces: Record<string, string[]>; leafCount: number };
  counts: { uiPages: number; apiRoutes: number; hubEvents: number; cliVerbs: number; i18nNamespaces: number };
}

function gitSha(dir: string): string {
  try {
    return execaSync("git", ["-C", dir, "rev-parse", "--short=12", "HEAD"]).stdout.trim();
  } catch {
    return "unknown";
  }
}

function walk(dir: string, filter: (name: string, full: string) => boolean, out: string[] = []): string[] {
  if (!fs.existsSync(dir)) return out;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name === ".next" || entry.name === "bin" || entry.name === "obj" || entry.name === ".git") continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, filter, out);
    else if (filter(entry.name, full)) out.push(full);
  }
  return out;
}

/** `/[locale]/(dashboard)/p/[projectId]/board/page.tsx` → `/p/[projectId]/board` */
export function normalizeRoute(appDir: string, file: string): string {
  let rel = path.relative(appDir, file).replace(/\\/g, "/");
  rel = rel.replace(/\/?page\.tsx$/, "");
  const segs = rel.split("/").filter((s) => s && s !== "[locale]" && !/^\(.*\)$/.test(s));
  return "/" + segs.join("/");
}

/**
 * The pages the Vite SPA actually routes: every `import("@/app/…/page")` in `src/routes.tsx`
 * (the page modules still live under `app/` in the folder layout their URLs mirror). Falls back to
 * walking `app/**\/page.tsx` when there is no route table.
 */
function scanPages(app: UiPage["app"], appRoot: string, root: string): UiPage[] {
  const appDir = path.join(appRoot, "app");
  const routesFile = path.join(appRoot, "src", "routes.tsx");
  let files: string[];
  if (fs.existsSync(routesFile)) {
    const text = fs.readFileSync(routesFile, "utf8");
    files = [...new Set([...text.matchAll(/import\(\s*"@\/app\/([^"]+\/)?page"\s*\)/g)].map((m) => path.join(appDir, m[1] ?? "", "page.tsx")))];
  } else {
    files = walk(appDir, (n) => n === "page.tsx");
  }
  return files
    .map((f) => ({ app, route: normalizeRoute(appDir, f), file: path.relative(root, f).replace(/\\/g, "/") }))
    .sort((a, b) => a.route.localeCompare(b.route));
}

/** FastEndpoints: `Get("/api/…")` in `Configure()`. */
const ROUTE_RE = /\b(Get|Post|Put|Patch|Delete)\(\s*"(\/[^"]+)"\s*\)/g;
/** Minimal APIs: `app.MapPost("/…"`, `group.MapGet("/…"` (a `var g = app.MapGroup("/x")` prefix is applied). */
const MAP_RE = /\b(\w+)\.Map(Get|Post|Put|Patch|Delete)\(\s*"(\/[^"]*)"/g;
/** `app.MapMethods("/…", [HttpMethods.Get, HttpMethods.Head], …)`. */
const MAP_METHODS_RE = /\b(\w+)\.MapMethods\(\s*"(\/[^"]*)"\s*,\s*\[([^\]]*)\]/g;
const GROUP_RE = /\bvar\s+(\w+)\s*=\s*\w+\.MapGroup\(\s*"(\/[^"]*)"\s*\)/g;
const CLASS_RE = /\b(?:sealed\s+|public\s+|internal\s+|file\s+)*class\s+(\w+)/g;

/** Every project that declares HTTP routes, and how its routes are classified. */
const API_SOURCES: { dir: string[]; host: ApiRoute["host"] }[] = [
  { dir: ["src", "Modules"], host: "api" },
  { dir: ["src", "Orkka.Local"], host: "local" },
  { dir: ["src", "Orkka.Runners.Server"], host: "relay" },
];

function scanApiRoutes(apiRoot: string): ApiRoute[] {
  const out: ApiRoute[] = [];
  for (const source of API_SOURCES) {
    const dir = path.join(apiRoot, ...source.dir);
    const host = source.host;
    for (const file of walk(dir, (n) => /Endpoint.*\.cs$/.test(n) || /Endpoints\.cs$/.test(n))) {
      const text = fs.readFileSync(file, "utf8");
      // Split into class blocks by class declaration positions.
      const classes: { name: string; start: number }[] = [];
      for (const m of text.matchAll(CLASS_RE)) classes.push({ name: m[1], start: m.index ?? 0 });
      const blockFor = (idx: number) => {
        let cur = classes[0];
        for (const c of classes) if (c.start <= idx) cur = c;
        return cur;
      };
      const blockText = (c: { start: number }) => {
        const next = classes.find((x) => x.start > c.start);
        return text.slice(c.start, next?.start ?? text.length);
      };
      for (const m of text.matchAll(ROUTE_RE)) {
        const cls = classes.length ? blockFor(m.index ?? 0) : { name: path.basename(file, ".cs"), start: 0 };
        const body = classes.length ? blockText(cls) : text;
        const perms = [...new Set([...body.matchAll(/Permission\.([A-Z][A-Za-z]+)/g)].map((p) => p[1]))].sort();
        out.push({ method: m[1].toUpperCase(), path: m[2], file: path.relative(apiRoot, file).replace(/\\/g, "/"), class: cls.name, permissions: perms, host });
      }
      // Minimal APIs (the host's /local/*, /launch, /blobs/*; the /internal/runner/* relays).
      const groups = new Map<string, string>();
      for (const g of text.matchAll(GROUP_RE)) groups.set(g[1], g[2]);
      const rel = path.relative(apiRoot, file).replace(/\\/g, "/");
      const cls = classes[0]?.name ?? path.basename(file, ".cs");
      const join = (target: string, p: string) => ((groups.get(target) ?? "") + p).replace(/\/+$/, "") || "/";
      for (const m of text.matchAll(MAP_RE)) {
        out.push({ method: m[2].toUpperCase(), path: join(m[1], m[3]), file: rel, class: cls, permissions: [], host });
      }
      for (const m of text.matchAll(MAP_METHODS_RE)) {
        for (const verb of m[3].matchAll(/HttpMethods\.(\w+)/g)) {
          out.push({ method: verb[1].toUpperCase(), path: join(m[1], m[2]), file: rel, class: cls, permissions: [], host });
        }
      }
    }
  }
  // Framework-mapped routes no endpoint file declares: `app.MapHealthChecks("/health"…)` and the
  // MCP Streamable HTTP endpoint (`MapMcp("/mcp")`, stateless → POST only).
  for (const source of API_SOURCES) {
    for (const file of walk(path.join(apiRoot, ...source.dir), (n) => n.endsWith(".cs"))) {
      const text = fs.readFileSync(file, "utf8");
      const rel = path.relative(apiRoot, file).replace(/\\/g, "/");
      for (const m of text.matchAll(/\.MapHealthChecks\(\s*"(\/[^"]*)"/g))
        out.push({ method: "GET", path: m[1], file: rel, class: path.basename(file, ".cs"), permissions: [], host: source.host });
      for (const m of text.matchAll(/\.MapMcp\(\s*"(\/[^"]*)"/g))
        out.push({ method: "POST", path: m[1], file: rel, class: path.basename(file, ".cs"), permissions: [], host: source.host });
    }
  }
  return out.sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method));
}

function scanHubs(apiRoot: string) {
  const modules = path.join(apiRoot, "src", "Modules");
  const hubs: SurfaceManifest["hubs"] = [];
  for (const file of walk(modules, (n) => /Hub\.cs$/.test(n))) {
    const text = fs.readFileSync(file, "utf8");
    const name = path.basename(file, ".cs");
    const joins = [...text.matchAll(/public\s+(?:async\s+)?Task\s+((?:Join|Leave)\w+)\(/g)].map((m) => m[1]);
    const events = [...new Set([...text.matchAll(/SendAsync\(\s*"([A-Za-z]+)"/g)].map((m) => m[1]))].sort();
    const mapMatch = [...walk(modules, (n) => n.endsWith("ModuleExtensions.cs"))]
      .map((f) => fs.readFileSync(f, "utf8"))
      .map((t) => new RegExp(`MapHub<${name}>\\("([^"]+)"\\)`).exec(t)?.[1])
      .find(Boolean);
    hubs.push({ path: mapMatch ?? "?", hub: name, joinMethods: joins, events });
  }
  // Events are broadcast from notifier services too; collect every SendAsync("X") in the modules.
  const allEvents = new Set<string>();
  for (const file of walk(modules, (n) => n.endsWith(".cs"))) {
    const text = fs.readFileSync(file, "utf8");
    for (const m of text.matchAll(/SendAsync\(\s*"([A-Z][A-Za-z]+)"/g)) allEvents.add(m[1]);
  }
  // The ProjectHub (provisioning/deployment events) was removed with managed mode; every
  // remaining notifier broadcast goes to TaskHub clients.
  const task = hubs.find((h) => h.hub === "TaskHub");
  if (task) task.events = [...allEvents].sort();
  return hubs.sort((a, b) => a.hub.localeCompare(b.hub));
}

function scanCliVerbs(apiRoot: string): string[] {
  // `./orkka <verb>` — the one entry point (src/Orkka, assembly `orkka`) dispatches every verb.
  const file = path.join(apiRoot, "src", "Orkka", "Program.cs");
  if (!fs.existsSync(file)) return [];
  const text = fs.readFileSync(file, "utf8");
  const verbs = new Set<string>();
  // `case "help" or "--help" or "-h" or null:` — flags and the `null` default are not verbs.
  for (const m of text.matchAll(/case\s+((?:"[^"]+"\s*(?:or\s+)?)+)(?:null\s*)?:/g)) {
    for (const v of m[1].matchAll(/"([^"]+)"/g)) if (!v[1].startsWith("-")) verbs.add(v[1]);
  }
  return [...verbs].sort();
}

function scanI18n(uiRoot: string) {
  const dir = path.join(uiRoot, "packages", "i18n", "messages", "en");
  const namespaces: Record<string, string[]> = {};
  let leafCount = 0;
  const countLeaves = (v: unknown): number => (v && typeof v === "object" ? Object.values(v as Record<string, unknown>).reduce((s: number, x) => s + countLeaves(x), 0) : 1);
  for (const f of ["common.json", "dashboard.json", "landing.json"]) {
    const full = path.join(dir, f);
    if (!fs.existsSync(full)) continue;
    const json = JSON.parse(fs.readFileSync(full, "utf8")) as Record<string, unknown>;
    namespaces[f.replace(".json", "")] = Object.keys(json).sort();
    leafCount += countLeaves(json);
  }
  return { namespaces, leafCount };
}

export function buildManifest(): SurfaceManifest {
  const ui = env.E2E_SRC_UI;
  const api = env.E2E_SRC_API;
  const uiPages = [
    ...scanPages("local", path.join(ui, "apps", "local"), ui),
  ];
  const apiRoutes = scanApiRoutes(api);
  const hubs = scanHubs(api);
  const cliVerbs = scanCliVerbs(api);
  const i18n = scanI18n(ui);
  return {
    generatedAt: new Date().toISOString(),
    sources: { ui: { path: ui, sha: gitSha(ui) }, api: { path: api, sha: gitSha(api) } },
    uiPages,
    apiRoutes,
    hubs,
    cliVerbs,
    i18n,
    counts: {
      uiPages: uiPages.length,
      apiRoutes: new Set(apiRoutes.map((r) => `${r.method} ${r.path}`)).size,
      hubEvents: hubs.reduce((s, h) => s + h.events.length, 0),
      cliVerbs: cliVerbs.length,
      i18nNamespaces: Object.values(i18n.namespaces).reduce((s, a) => s + a.length, 0),
    },
  };
}

/** `/api/tasks/{taskId:ulid}/plans/{planId}` → `/api/tasks/{*}/plans/{*}` */
export function normalizeApiPath(p: string): string {
  return p.replace(/\{[^}]+\}/g, "{*}").replace(/\/+$/, "").toLowerCase();
}
