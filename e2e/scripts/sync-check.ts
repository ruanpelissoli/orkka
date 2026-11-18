/**
 * Keep-in-sync gate: (1) the committed surface manifest must match the sources (src/, ui/), (2) every
 * page, API route, hub event, CLI verb and i18n namespace must be referenced by at least one
 * catalogue case (or be listed with a reason in catalog/sync-ignore.json), and (3) no live case may
 * reference surface that does not exist — a route, page, namespace, verb, hub event or source file
 * that is gone (deliberate exceptions: sync-ignore.json → danglingRefs, keyed "<CASE-ID> <ref>",
 * each with a reason). Exit 1 on any gap.
 * `npm run sync-check`
 */
import fs from "node:fs";
import path from "node:path";
import { paths } from "../config/index.js";
import { buildManifest, normalizeApiPath, type SurfaceManifest } from "../src/catalog/manifest.js";
import { loadCatalog } from "../src/catalog/parse.js";
import { diffManifests } from "./surface-manifest.js";

interface Ignore {
  apiRoutes?: Record<string, string>;
  uiPages?: Record<string, string>;
  i18nNamespaces?: Record<string, string>;
  cliVerbs?: Record<string, string>;
  hubEvents?: Record<string, string>;
  /** `"<CASE-ID> <ref>": "reason"` — a live case that deliberately names surface that is gone. */
  danglingRefs?: Record<string, string>;
}

const manifestFile = path.join(paths.catalog, "surface-manifest.json");
const ignoreFile = path.join(paths.catalog, "sync-ignore.json");
const ignore: Ignore = fs.existsSync(ignoreFile) ? JSON.parse(fs.readFileSync(ignoreFile, "utf8")) : {};
const committed = fs.existsSync(manifestFile) ? (JSON.parse(fs.readFileSync(manifestFile, "utf8")) as SurfaceManifest) : undefined;
const fresh = buildManifest();
let failures = 0;
const section = (title: string, items: string[]) => {
  if (!items.length) return;
  failures += items.length;
  console.log(`\n${title} (${items.length}):`);
  for (const i of items) console.log(`  - ${i}`);
};

// 1. Manifest staleness.
if (!committed) {
  console.log("No committed manifest — run `npm run manifest:update` after adding cases.");
  failures++;
} else {
  const d = diffManifests(committed, fresh);
  section("NEW API routes not in the committed manifest", d.newRoutes);
  section("REMOVED API routes still in the committed manifest", d.removedRoutes);
  section("NEW UI pages", d.newPages);
  section("REMOVED UI pages", d.removedPages);
  section("NEW i18n namespaces", d.newNamespaces);
  section("REMOVED i18n namespaces", d.removedNamespaces);
  section("NEW CLI verbs", d.newVerbs);
  section("REMOVED CLI verbs", d.removedVerbs);
  section("NEW hub events", d.newEvents);
  section("REMOVED hub events", d.removedEvents);
}

// 2. Catalogue coverage of the fresh surface.
const { cases, issues } = loadCatalog();
if (issues.length) {
  console.log(`\ncatalogue has ${issues.length} validation issue(s) — run npm run catalog:index`);
  failures += issues.length;
}
const refs = cases.flatMap((c) => c.refs);
const matchOr = (re: RegExp) => (r: string) => re.exec(r)?.[1];
const refRoutes = new Set(refs.map(matchOr(/^orkka-api:((?:GET|HEAD|POST|PUT|PATCH|DELETE) \/.+)$/)).filter(Boolean).map((r) => normalizeRouteKey(r!)));
const refPages = new Set(refs.map(matchOr(/^orkka-ui:((?:local:)?\/.*)$/)).filter(Boolean).map((p) => (p!.startsWith("local:") ? `local:${p!.slice(6)}` : `local:${p}`)));
const refAdmin = new Set(refs.map(matchOr(/^admin-ui:(\/.*)$/)).filter(Boolean).map((p) => `admin:${p}`));
const refNs = new Set(refs.map(matchOr(/^i18n:([A-Za-z0-9_.-]+)$/)).filter(Boolean) as string[]);
const refVerbs = new Set(refs.map(matchOr(/^cli:([a-z-]+)$/)).filter(Boolean) as string[]);
const refEvents = new Set(refs.map(matchOr(/^hub:([A-Za-z]+\.[A-Za-z]+)$/)).filter(Boolean) as string[]);

function normalizeRouteKey(r: string) {
  const [method, p] = r.split(" ");
  return `${method.toUpperCase()} ${normalizeApiPath(p)}`;
}

const uncoveredRoutes = [...new Set(fresh.apiRoutes.map((r) => `${r.method} ${r.path}`))]
  .filter((k) => !refRoutes.has(normalizeRouteKey(k)))
  .filter((k) => !Object.keys(ignore.apiRoutes ?? {}).some((ig) => normalizeRouteKey(ig) === normalizeRouteKey(k) || globMatch(ig, k)));
const uncoveredPages = fresh.uiPages
  .map((p) => `${p.app}:${p.route}`)
  .filter((k) => !(refPages.has(k) || refAdmin.has(k)))
  .filter((k) => !Object.keys(ignore.uiPages ?? {}).some((ig) => ig === k || globMatch(ig, k)));
const uncoveredNs = Object.entries(fresh.i18n.namespaces)
  .flatMap(([file, keys]) => keys.map((k) => ({ file, k })))
  .filter(({ k }) => !refNs.has(k))
  .map(({ file, k }) => `${file}:${k}`)
  .filter((k) => !Object.keys(ignore.i18nNamespaces ?? {}).some((ig) => ig === k || ig === k.split(":")[1] || globMatch(ig, k)));
const uncoveredVerbs = fresh.cliVerbs.filter((v) => !refVerbs.has(v) && !(ignore.cliVerbs ?? {})[v]);
const uncoveredEvents = fresh.hubs.flatMap((h) => h.events.map((e) => `${h.hub}.${e}`)).filter((e) => !refEvents.has(e) && !(ignore.hubEvents ?? {})[e]);

section("API routes with no catalogue case", uncoveredRoutes);
section("UI pages with no catalogue case", uncoveredPages);
section("i18n namespaces with no catalogue case", uncoveredNs);
section("CLI verbs with no catalogue case", uncoveredVerbs);
section("Hub events with no catalogue case", uncoveredEvents);

// 3. Live cases must not reference surface that is gone.
const liveRoutes = new Set(fresh.apiRoutes.map((r) => normalizeRouteKey(`${r.method} ${r.path}`)));
const livePages = new Set(fresh.uiPages.map((p) => p.route));
const liveNs = new Set(Object.values(fresh.i18n.namespaces).flat());
const liveVerbs = new Set(fresh.cliVerbs);
const liveEvents = new Set(fresh.hubs.flatMap((h) => h.events.map((e) => `${h.hub}.${e}`)));
const sourceRoots: Record<string, string> = { "orkka-api": fresh.sources.api.path, "orkka-ui": fresh.sources.ui.path };

/** true = the surface exists, false = it is gone, undefined = a ref form this check does not judge. */
function refIsLive(ref: string): boolean | undefined {
  let m: RegExpExecArray | null;
  if ((m = /^orkka-api:((?:GET|HEAD|POST|PUT|PATCH|DELETE) \/.+)$/.exec(ref))) return liveRoutes.has(normalizeRouteKey(m[1]));
  if ((m = /^orkka-ui:(?:local:)?(\/.*)$/.exec(ref))) return livePages.has(m[1]);
  if ((m = /^i18n:([A-Za-z0-9_-]+)$/.exec(ref))) return liveNs.has(m[1]);
  if ((m = /^cli:(.+)$/.exec(ref))) return liveVerbs.has(m[1]);
  if ((m = /^hub:([A-Za-z]+\.[A-Za-z]+)$/.exec(ref))) return liveEvents.has(m[1]);
  // Documentation refs to a source file (`orkka-api:src/…`, `orkka-ui:packages/…`) must still exist.
  if ((m = /^(orkka-api|orkka-ui):([^\s/][^\s]*\.[A-Za-z]+)$/.exec(ref))) return fs.existsSync(path.join(sourceRoots[m[1]], m[2]));
  return undefined;
}

const allowedDangling = ignore.danglingRefs ?? {};
const usedAllowances = new Set<string>();
const danglingAll = cases
  .filter((c) => c.automation !== "retired")
  .flatMap((c) => c.refs.filter((r) => refIsLive(r) === false).map((r) => `${c.id} ${r}`));
const dangling = danglingAll.filter((k) => {
  if (allowedDangling[k] === undefined) return true;
  usedAllowances.add(k);
  return false;
});
section("Live cases referencing surface that no longer exists (retire or rewrite the case, or allow-list it in sync-ignore.json → danglingRefs with a reason)", dangling);
section("Stale danglingRefs allowances (no live case carries that ref any more, or the surface is back)", Object.keys(allowedDangling).filter((k) => !usedAllowances.has(k)));

function globMatch(pattern: string, value: string) {
  if (!pattern.includes("*")) return false;
  const re = new RegExp("^" + pattern.split("*").map((s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join(".*") + "$", "i");
  return re.test(value);
}

console.log(`\nsync-check: ${failures === 0 ? "OK" : `${failures} gap(s)`} — ${cases.length} cases (${usedAllowances.size} allowed dangling refs) cover ${refRoutes.size} routes, ${refPages.size + refAdmin.size} pages, ${refNs.size} namespaces, ${refVerbs.size} CLI verbs, ${refEvents.size} hub events`);
process.exit(failures === 0 ? 0 : 1);
