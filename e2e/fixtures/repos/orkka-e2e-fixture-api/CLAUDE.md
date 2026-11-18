# CLAUDE.md — orkka-e2e-fixture-api

Dependency-free Node (ESM) HTTP API used by automated end-to-end tests.

## Commands
- `npm test` — `node --test test/`
- `npm run lint` — syntax check
- `npm start` — serve on http://127.0.0.1:4000 (`/health`, `/features`)

## Conventions
- The feature list lives in `src/features.js` (`export const features`). When asked to add a feature entry, append the exact text given as a new string at the END of that array and change nothing else.
- No dependencies; do not add a package.
- Run `npm test` before finishing.
