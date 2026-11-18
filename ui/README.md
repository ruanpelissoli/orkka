# orkka-ui

The Orkka web UI, as an npm workspaces monorepo. Orkka is local-only: the host on 127.0.0.1:6750
serves the API and this UI's built files — a static single-page app, no Node at runtime.

| Path | What it is |
|------|------------|
| `apps/local` | The Orkka UI (`@orkka/local-ui`). Vite + React Router SPA; `npm run build` → `apps/local/dist`. See `apps/local/README.md` for how the host serves it. |
| `packages/ui` | Shared React components, consumed as source through the `@/*` alias. |
| `packages/core` | API client, types, hooks and pure helpers. |
| `packages/i18n` | Translation catalogs for `en`, `es`, `pt`, `fr` and `de`. |

## Scripts

Run from this folder (`ui/`):

```bash
npm ci
npm run build       # vite build → apps/local/dist (index.html + hashed assets)
npm run dev         # vite dev server on :5173, proxying the API to a running host on :6750
npm run preview     # serve the built dist/ on :4173
npm run typecheck
npm run lint        # one eslint config (eslint.config.mjs) for the app and the packages
npm test            # vitest
```

Node is needed to build, never to run.
