# orkka-e2e-fixture-api

A dependency-free Node HTTP API that Orkka's end-to-end test suite uses as a second
"bring your own repo" target. Tests ask the AI dev team to add entries to `src/features.js`.

- Check command: `npm test`
- Run: `npm start` (port 4000) — see `orkka.json`
- `main` may be rewritten by `npm run fixtures:publish` in the e2e repo.
