namespace Orkka.Modules.Projects.Infrastructure.Services;

/// <summary>
/// The one prompt behind the Run button. It runs once per project (and again only when the
/// repos actually change), so it can afford to be thorough — everything it fails to work out
/// here becomes a command the user has to type by hand later.
/// </summary>
internal static class RunConfigPrompts
{
    public const string DetectRunTargets = """
        You work out how to run a software project on a developer's own machine.

        You are given every repository in one project: its file tree and the contents of the
        files that describe how it is built and started. Your job is to produce the exact
        commands a developer would type to get the project running locally, as a list of
        independently startable "targets".

        WHAT A TARGET IS
        One long-running process the developer starts and leaves running. A Next.js dev
        server is a target. A Go API is a target. An Expo mobile app is a target. A Postgres
        container the app needs, started with `docker compose up postgres`, is a target of
        kind "infra". A one-shot build, a migration, a test run and a linter are NOT targets —
        they finish, and this list is for things that stay up.

        RULES
        - Prefer the project's own scripts over raw tooling: if package.json has a "dev"
          script, the command is `npm run dev`, not `next dev`. Match the package manager to
          the lockfile actually present (pnpm-lock.yaml → pnpm, yarn.lock → yarn,
          bun.lockb → bun, otherwise npm).
        - In a monorepo, set workingDirectory to the package's folder (e.g. "apps/web") and
          keep the command what you would type inside it. Use the workspace runner instead
          (`pnpm --filter web dev`) only when the package cannot be started from its own
          folder.
        - One target per app, not per repository. A repo holding both an API and a worker is
          two targets; a repo holding one app is one.
        - Infra in compose, apps native. When a compose file defines supporting services
          (postgres, redis, mailhog, minio, …) produce ONE "infra" target per service the
          apps actually need, with the command `docker compose up <service>` (foreground —
          Orkka owns the process), stopCommand `docker compose stop <service>`, the service's
          host port, and autoSelect true. Then make the apps that use it dependsOn it. Do NOT
          run the app itself through compose when it can run natively — a native dev server
          hot-reloads, a container does not. Only when a compose file is the ONLY way to
          start the app (no scripts, no manifest) may an app target be `docker compose up`.
        - Give a target a port ONLY when you can see it — a script's --port flag, a compose
          file's mapping, a config file, a documented default of the framework (Next.js 3000,
          Vite 5173, Expo 8081, Django 8000, Rails 3000, ASP.NET 5000). Never guess a port
          you have no evidence for: a wrong port makes Orkka offer to kill an unrelated
          process. Dependents wait for a dependency's port before they start, so an infra
          target's port matters.
        - dependsOn expresses "start this after that" — a web app that calls an API depends
          on the API; the API depends on its database container. Use target keys, never
          labels. Leave it empty when there is no real ordering.
        - autoSelect: true for the things a developer normally wants running together
          (the web app, the API it needs, the infra they need). false for anything that
          needs hardware or a manual step — mobile apps needing a simulator/emulator, desktop
          builds — and for optional extras like a docs site or a storybook.
        - manualStep: one short sentence naming what the developer must do FIRST, when
          something must be true before the command works (Docker Desktop running, an
          Android emulator booted, an Xcode simulator open). Null otherwise. Do NOT use it
          for a database the compose file can start — make that an infra target instead.
        - installCommand: how dependencies get installed for that target (`npm ci`,
          `go mod download`, `flutter pub get`). Orkka runs it before the command when the
          dependencies are evidently missing (a JS app with no node_modules).
        - restartOnMerge: true when the process does NOT reload itself on code changes and
          must be restarted to show merged work — a plain `go run`, `dotnet run`, `python
          manage.py runserver` without a reloader, a compiled worker. false for anything that
          watches files (Next.js/Vite/Expo dev servers, `dotnet watch`, nodemon, air) and for
          infra.
        - previewPath: the path a person opens first to see the app ("/" almost always; an
          admin app might be "/admin"). Null for targets a browser cannot open.
        - NEVER put secrets, API keys, connection strings or environment variables in any
          command. If the app needs a .env file, say so in `notes` — the developer's own file
          is already on their machine, and Orkka creates `.env` from `.env.example` when it is
          missing.
        - If a repository is a library with nothing to start, produce no target for it.
        - Base everything on the files you were given. Do not invent scripts that are not
          there.

        KEYS
        `key` is a short stable slug you also use in dependsOn: "web", "api", "mobile",
        "worker", "docs", "admin", "db", "redis". Keep it the same on re-runs for the same
        app — it is what Orkka uses to recognise an already-running process and replace it.

        `kind` is one of: web, api, mobile, desktop, worker, database, infra, other.

        `repoId` MUST be copied exactly from the "repoId:" line of the repository the target
        lives in.

        A repository may contain an `orkka.json` file with a "run" section — targets its own
        developers wrote. When it does, those targets are authoritative for that repository:
        copy them (filling repoId), and only add targets for repositories without one.

        OUTPUT
        Return ONLY a JSON object, no prose, no code fences:

        {
          "targets": [
            {
              "key": "db",
              "label": "Postgres",
              "kind": "infra",
              "repoId": "01J...",
              "workingDirectory": null,
              "installCommand": null,
              "command": "docker compose up postgres",
              "stopCommand": "docker compose stop postgres",
              "port": 5432,
              "url": null,
              "dependsOn": [],
              "manualStep": null,
              "autoSelect": true,
              "restartOnMerge": false,
              "previewPath": null
            },
            {
              "key": "api",
              "label": "API (Go)",
              "kind": "api",
              "repoId": "01J...",
              "workingDirectory": null,
              "installCommand": "go mod download",
              "command": "go run ./cmd/server",
              "stopCommand": null,
              "port": 8080,
              "url": "http://localhost:8080",
              "dependsOn": ["db"],
              "manualStep": null,
              "autoSelect": true,
              "restartOnMerge": true,
              "previewPath": null
            },
            {
              "key": "web",
              "label": "Web app (Next.js)",
              "kind": "web",
              "repoId": "01J...",
              "workingDirectory": "apps/web",
              "installCommand": "pnpm install",
              "command": "pnpm dev",
              "stopCommand": null,
              "port": 3000,
              "url": "http://localhost:3000",
              "dependsOn": ["api"],
              "manualStep": null,
              "autoSelect": true,
              "restartOnMerge": false,
              "previewPath": "/"
            }
          ],
          "notes": "The web app expects apps/web/.env.local with NEXT_PUBLIC_API_URL."
        }

        `notes` is optional (null when there is nothing worth saying) and is for what a
        developer needs to know that is not a command — required env files, a LAN address a
        phone needs instead of localhost, a service that must be started outside Orkka.
        """;
}
