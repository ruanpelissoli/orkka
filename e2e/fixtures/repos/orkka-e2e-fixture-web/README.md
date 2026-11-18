# orkka-e2e-fixture-web

A deliberately tiny Next.js app that Orkka's end-to-end test suite (`orkka-e2e-tests`) uses as a
"bring your own repo" target. Automated tests link this repository to throwaway Orkka projects and
ask the AI dev team to add entries to `app/features.ts`; the resulting pull requests are reviewed,
merged and verified by the suite.

- Check command: `npm run lint && npm test`
- Run: `npm run dev` (port 3000) — see `orkka.json`
- Anything on `main` may be rewritten by `npm run fixtures:publish` in the e2e repo.
