import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import type { Plugin } from "vite";

/**
 * Serves Excalidraw's own fonts (Excalifont, Virgil, Xiaolai…) from this app at
 * `/excalidraw/fonts/…`. Without it Excalidraw downloads them from esm.sh at runtime — an
 * outbound request from a local-only tool, and one the host's `font-src 'self'` CSP blocks.
 * ExcalidrawWrapper points `window.EXCALIDRAW_ASSET_PATH` here.
 */
export const EXCALIDRAW_ASSET_BASE = "/excalidraw/";

// The package exports only its entry (dist/prod/index.js); the fonts sit beside it.
const fontsDir = path.join(
  path.dirname(createRequire(import.meta.url).resolve("@excalidraw/excalidraw")),
  "fonts"
);

function listFiles(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    return entry.isDirectory() ? listFiles(full) : [full];
  });
}

export function excalidrawFonts(): Plugin {
  const prefix = `${EXCALIDRAW_ASSET_BASE}fonts/`;
  return {
    name: "orkka-excalidraw-fonts",
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const url = req.url?.split("?")[0] ?? "";
        if (!url.startsWith(prefix)) return next();
        const file = path.join(fontsDir, decodeURIComponent(url.slice(prefix.length)));
        if (!file.startsWith(fontsDir) || !fs.existsSync(file)) return next();
        res.setHeader("Content-Type", "font/woff2");
        fs.createReadStream(file).pipe(res);
      });
    },
    generateBundle() {
      for (const file of listFiles(fontsDir)) {
        this.emitFile({
          type: "asset",
          fileName: `${prefix.slice(1)}${path.relative(fontsDir, file).split(path.sep).join("/")}`,
          source: fs.readFileSync(file),
        });
      }
    },
  };
}
