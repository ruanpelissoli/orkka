/**
 * Scan the sibling source repos and write catalog/surface-manifest.json (pages, API routes with
 * permissions, hubs, CLI verbs, i18n namespaces). `npm run manifest` prints a summary and a diff
 * against the committed manifest; `--update` rewrites it.
 */
import fs from "node:fs";
import path from "node:path";
import { paths } from "../config/index.js";
import { buildManifest, type SurfaceManifest } from "../src/catalog/manifest.js";

const update = process.argv.includes("--update");
const file = path.join(paths.catalog, "surface-manifest.json");
const fresh = buildManifest();
const prev = fs.existsSync(file) ? (JSON.parse(fs.readFileSync(file, "utf8")) as SurfaceManifest) : undefined;

console.log(`surface: ${fresh.counts.uiPages} pages · ${fresh.counts.apiRoutes} API routes · ${fresh.counts.hubEvents} hub events · ${fresh.counts.cliVerbs} CLI verbs · ${fresh.counts.i18nNamespaces} i18n namespaces (${fresh.i18n.leafCount} strings)`);
console.log(`sources: ui@${fresh.sources.ui.sha} api@${fresh.sources.api.sha}`);

export function diffManifests(a: SurfaceManifest | undefined, b: SurfaceManifest) {
  const key = (r: { method: string; path: string }) => `${r.method} ${r.path}`;
  const setOf = <T>(xs: T[] | undefined, f: (x: T) => string) => new Set((xs ?? []).map(f));
  const diff = (x: Set<string>, y: Set<string>) => [...y].filter((v) => !x.has(v)).sort();
  const aRoutes = setOf(a?.apiRoutes, key);
  const bRoutes = setOf(b.apiRoutes, key);
  const aPages = setOf(a?.uiPages, (p) => `${p.app}:${p.route}`);
  const bPages = setOf(b.uiPages, (p) => `${p.app}:${p.route}`);
  const aNs = new Set(Object.entries(a?.i18n.namespaces ?? {}).flatMap(([f, ks]) => ks.map((k) => `${f}:${k}`)));
  const bNs = new Set(Object.entries(b.i18n.namespaces).flatMap(([f, ks]) => ks.map((k) => `${f}:${k}`)));
  const aVerbs = new Set(a?.cliVerbs ?? []);
  const bVerbs = new Set(b.cliVerbs);
  const aEv = new Set((a?.hubs ?? []).flatMap((h) => h.events.map((e) => `${h.hub}.${e}`)));
  const bEv = new Set(b.hubs.flatMap((h) => h.events.map((e) => `${h.hub}.${e}`)));
  return {
    newRoutes: diff(aRoutes, bRoutes),
    removedRoutes: diff(bRoutes, aRoutes),
    newPages: diff(aPages, bPages),
    removedPages: diff(bPages, aPages),
    newNamespaces: diff(aNs, bNs),
    removedNamespaces: diff(bNs, aNs),
    newVerbs: diff(aVerbs, bVerbs),
    removedVerbs: diff(bVerbs, aVerbs),
    newEvents: diff(aEv, bEv),
    removedEvents: diff(bEv, aEv),
  };
}

const isMain = process.argv[1]?.replace(/\\/g, "/").endsWith("scripts/surface-manifest.ts");
if (isMain) {
  if (prev) {
    const d = diffManifests(prev, fresh);
    const total = Object.values(d).reduce((s, a) => s + a.length, 0);
    if (total === 0) console.log("no surface changes since the committed manifest");
    for (const [k, v] of Object.entries(d)) if (v.length) console.log(`${k} (${v.length}):\n  ${v.join("\n  ")}`);
  } else console.log("no committed manifest yet");
  if (update || !prev) {
    fs.writeFileSync(file, JSON.stringify(fresh, null, 2) + "\n");
    console.log(`wrote ${path.relative(paths.root, file)}`);
  }
}
