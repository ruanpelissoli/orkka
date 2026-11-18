# CLAUDE.md — orkka-e2e-fixture-web

Minimal Next.js 15 (App Router) + TypeScript app used by automated end-to-end tests.

## Commands
- `npm install` — install
- `npm run lint` — typecheck (`tsc --noEmit`)
- `npm test` — unit tests (vitest)
- `npm run dev` — dev server on http://localhost:3000

## Conventions
- The list of features rendered on the homepage lives in `app/features.ts` (`export const features: string[]`). When asked to add a feature entry, append the exact text given as a new string at the END of that array and change nothing else.
- Keep changes minimal; do not add dependencies, styling systems or new pages unless the task asks for them.
- Always run `npm run lint && npm test` before finishing.
