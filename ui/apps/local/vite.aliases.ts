import path from "node:path";
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import { normalizePath, type Plugin } from "vite";

/**
 * The `@/*` import alias, shared by vite.config.ts (dev + build) and vitest.config.mts.
 *
 * Same fallback chain as tsconfig's `paths`: `@/x` is looked up in this app, then in
 * packages/ui/src, then in packages/core/src — the first that has the file wins. That lets the
 * shared packages import app-provided modules (`@/lib/actions`, `@/i18n/navigation`) and the
 * app override a shared file by putting one at the same path.
 */
const appRoot = path.resolve(fileURLToPath(new URL(".", import.meta.url)));

const ROOTS = [
  appRoot,
  path.resolve(appRoot, "../../packages/ui/src"),
  path.resolve(appRoot, "../../packages/core/src"),
];

const SUFFIXES = ["", ".tsx", ".ts", "/index.tsx", "/index.ts"];

function isFile(file: string): boolean {
  try {
    return fs.statSync(file).isFile();
  } catch {
    return false;
  }
}

/** Resolves `@/…` to a file the way tsconfig does (TypeScript's extension and index lookup). */
export function resolveAppAlias(source: string): string | null {
  if (!source.startsWith("@/")) return null;
  const rest = source.slice(2);
  for (const root of ROOTS) {
    for (const suffix of SUFFIXES) {
      const candidate = path.join(root, rest + suffix);
      // Forward slashes, like every id Vite resolves itself: the same file reached by a
      // relative import and by `@/…` must be ONE module (one React context, not two).
      if (isFile(candidate)) return normalizePath(candidate);
    }
  }
  return null;
}

export function appAlias(): Plugin {
  return {
    name: "orkka-app-alias",
    enforce: "pre",
    resolveId(source) {
      return resolveAppAlias(source);
    },
  };
}
