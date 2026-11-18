# @orkka/local-ui (apps/local)

The Orkka UI: a static single-page app (Vite + React Router), served by the Orkka host on the
developer's own machine. There is no account, no sign-in page and no Node process at runtime.

## Build

```bash
npm ci            # from ui/ — a clean install needs no install scripts approved
npm run build     # vite build → apps/local/dist/
```

`dist/` is plain files:

| Path | What |
|------|------|
| `index.html` | The only HTML page. Loads `/assets/index-<hash>.js` and `/assets/index-<hash>.css`; no inline script or style. |
| `assets/**` | Hashed JS chunks (one per page, plus Excalidraw, xterm, shiki…), CSS, fonts and workers. Immutable. |
| `excalidraw/fonts/**` | Excalidraw's own fonts, self-hosted so the diagram editor never calls its CDN (`vite.excalidraw-fonts.ts`). File names carry a content hash. |
| `favicon.ico`, `orkka-favicon.svg`, `orkka-*.svg`, `orkka-logo-email.png` | `public/`, copied as-is. |

## How it is served — the contract for the host

The host serves `dist/` itself (configurable directory). Every path below is same-origin with the
API: `/api/*`, `/hubs/*`, `/blobs/*`, `/mcp`, `/launch`, `/local/*`, `/internal/*` and `/health`
are the host's own and never reach these rules.

For every other `GET`/`HEAD`:

1. **Static file.** If the path names a file in `dist/` (URL-decoded, no `..`, no directory
   listing), serve it with its content type.
   - `/assets/*` and `/excalidraw/fonts/*`: `Cache-Control: public, max-age=31536000, immutable`.
   - Everything else (`index.html`, the favicon, logos): `Cache-Control: no-cache`.
   - A path under `/assets/` that does not exist is a plain **404**, never `index.html`: a tab
     still running an older build then fails to load a chunk, and the app's error page
     (`app/[locale]/error.tsx`) reloads into the new build instead of parsing HTML as JavaScript.
2. **Locale redirect.** `/`, and any path whose first segment is not one of the UI's locales
   (`en`, `es`, `pt`, `fr`, `de`), gets a **302** to `/{locale}{path}{?query}` — `/` goes to
   `/{locale}`. The locale is, in order:
   1. the `NEXT_LOCALE` cookie, when it holds one of the five;
   2. the first `Accept-Language` entry (by `q`, ties in order) whose primary subtag is one of the
      five (`pt-BR` → `pt`);
   3. `en`.
   (`i18n/routing.ts` → `preferredLocale` applies the same order in the browser, with
   `navigator.languages`, so an unprefixed URL also works before the host rule exists.)
3. **SPA fallback.** Any other path — `/{locale}/…` that is not a file — returns `index.html`
   with **200**, `Content-Type: text/html; charset=utf-8` and `Cache-Control: no-cache`. React
   Router renders the page, or the translated 404 page for an unknown path. Deep links such as
   `/en/p/{id}/tasks/{n}` work on reload.

`index.html` (rules 2 and 3 only — never assets) is where the host's session gate applies: no
valid `orkka_s` cookie → the "signed out — run `./orkka open`" page instead.

Headers for `index.html`:

```
Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';
  img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws://127.0.0.1:{port} ws://localhost:{port};
  worker-src 'self' blob: data:; object-src 'none'; base-uri 'none'; form-action 'self';
  frame-ancestors 'none'
X-Content-Type-Options: nosniff
Referrer-Policy: no-referrer
X-Frame-Options: DENY
```

Why each exception:

- `script-src 'self'` — no inline script and no `eval`/`new Function` anywhere in the bundle.
- `style-src 'unsafe-inline'` — shiki's highlighted code carries `style="color:…"` attributes, and
  xterm.js and Excalidraw inject `<style>` elements at runtime.
- `img-src data: blob:` — Excalidraw renders and exports through data/blob URLs; attachment
  previews use blob URLs.
- `font-src data:` — a few bundled stylesheets inline small fonts.
- `connect-src` — fetch, SSE and SignalR are same-origin; the explicit `ws://` origins cover
  browsers that do not match `'self'` against WebSocket URLs.
- `worker-src blob: data:` — Excalidraw's image resizer (pica) starts its workers from blob/data
  URLs; its font subsetter is a same-origin worker file.

The bundle reads no environment variables and holds no secret: the API base is always the page's
own origin.

## Development

```bash
npm run dev       # vite on http://127.0.0.1:5173, hot reload
```

The dev server proxies `/api`, `/hubs` (WebSockets too), `/blobs`, `/mcp`, `/launch` and `/health`
to the host on `http://127.0.0.1:6750`, so a host must be running. It rewrites `Host` and
`Origin` to the host's own, which its request guard requires for cookie-authenticated writes.
Sign in once through the host (`/launch?code=…` on :6750): cookies ignore the port, so the
`orkka_s` session then works on :5173 as well. Limits: the dev server is a convenience for UI
work — it skips the host's session gate and static-file rules, and an unprefixed URL is
redirected by the SPA rather than the host. The real path is `dist/` served by the host.

`npm run preview` serves the built `dist/` on :4173 (Vite's preview server, with an SPA fallback).

## Checks

```bash
npm run typecheck   # tsc --noEmit
npm test            # vitest
npm run lint        # eslint, the app AND ../../packages (one config: ui/eslint.config.mjs)
```
