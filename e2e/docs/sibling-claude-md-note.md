# Paragraph for the repository's CLAUDE.md

Since the 2026-11 merge the catalogue lives in this repository (`e2e/catalog/`), next to the code it covers (`src/`, `ui/`) — there are no sibling repos. Keep this block in `.claude/CLAUDE.md` (or wherever feature work is briefed) so every feature change keeps the e2e catalogue in sync.

```markdown
## End-to-end test catalogue (e2e/)

Every page, endpoint, hub event, `./orkka` CLI verb and user-facing setting has a test case in
`e2e/catalog/*.md` (format: `e2e/catalog/README.md`). When you add, rename or remove one of those,
add or update the matching case in the right `catalog/NN-*.md`, then in `e2e/` run
`npm run catalog:index && npm run manifest:update`. `npm run sync-check` there must pass before you
finish — it fails on any surface with no case, and on any live case whose ref names a page, route,
namespace, verb or source file that is gone, so retire or rewrite those too (it scans `src/Orkka/Program.cs` for verbs,
`src/Modules`, `src/Orkka.Local` and `src/Orkka.Runners.Server` for routes, and
`ui/apps/local/src/routes.tsx` for pages). Never hand-edit the generated `index.json`, `COVERAGE.md`
or `surface-manifest.json`. When the runner's Jira/ADO calls (src/Orkka.LocalRunner/Trackers) or its
gh/az/glab calls (ForgeCli.cs) change, update the fake tracker (e2e/src/fakes/tracker) / stub gh
(e2e/fixtures/stub-gh) and run `npm run test:offline` there.
```
