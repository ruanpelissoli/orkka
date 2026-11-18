import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { appAlias } from "./vite.aliases";
import { excalidrawFonts } from "./vite.excalidraw-fonts";

/**
 * The Orkka UI is a static single-page app. `npm run build` writes plain files to dist/
 * (index.html + hashed assets) and the Orkka host serves them — no Node process at runtime.
 * The host's side of the contract (static files, SPA fallback, locale redirect, CSP) is in
 * README.md → "How it is served".
 */

/** The host. `npm run dev` proxies the API, hubs and blobs to it. */
const HOST = "http://127.0.0.1:6750";

export default defineConfig({
  plugins: [appAlias(), react(), tailwindcss(), excalidrawFonts()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    // Excalidraw and shiki are large on their own and load only on the pages that use them.
    chunkSizeWarningLimit: 4096,
  },
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    // Dev only. The host refuses cookie-authenticated writes whose Origin is not its own and
    // any Host it doesn't serve (LocalRequestGuard), so both are rewritten to the host's. The
    // orkka_s session cookie is shared because cookies ignore the port: sign in once through
    // the host (`/launch?code=…` on :6750), then use :5173.
    proxy: Object.fromEntries(
      ["/api", "/hubs", "/blobs", "/mcp", "/launch", "/health"].map((prefix) => [
        prefix,
        { target: HOST, changeOrigin: true, ws: prefix === "/hubs", headers: { origin: HOST } },
      ])
    ),
  },
  preview: { host: "127.0.0.1", port: 4173, strictPort: true },
});
