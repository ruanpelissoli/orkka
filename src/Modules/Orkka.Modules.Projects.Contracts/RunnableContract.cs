namespace Orkka.Modules.Projects.Contracts;

/// <summary>
/// The runnable contract: what every project Orkka builds must keep true so that the Run
/// button works and keeps working after every merge. The dev agent maintains it and the
/// QA/Run paths rely on it, so it lives in one place.
///
/// The shape (infra in compose, apps native, `.env.example` with working local defaults,
/// `orkka.json` naming the targets) is the shape that lets a person watch the app being
/// built: a native dev server hot-reloads on every merge, and a manifest means Orkka never
/// has to guess how to start it.
/// </summary>
public static class RunnableContract
{
    /// <summary>The section injected into the dev agent's system prompt.</summary>
    public const string DevAgentSection = """
        ## Keep the app runnable (Orkka run contract)
        `orkka.json` at the repository root is the file Orkka reads about this repo:
        - `stack` — the toolchain, exactly one of node | python | go | rust | jvm | dotnet |
          php | ruby | lua | cpp | mobile.
        - `setup` — prepares a fresh clone (install dependencies, copy `.env.example`);
          `check` — the quality gate run after every dev pass (lint + typecheck + tests);
          `build` — proves the code compiles.
        - `run.targets` (key, label, kind, command, installCommand, stopCommand, port,
          dependsOn, restartOnMerge, previewPath) — what the Run button starts on the
          developer's machine.
        The developer's machine runs these commands only once this file is on the default
        branch — i.e. after your change is reviewed and merged — so write them as the team
        would want them run. Whenever your change alters HOW the app installs, starts or is
        checked — a new service or dependency, a new required environment variable, a changed
        port or command, a migration or seed step — update, in the same task:
        - `orkka.json` — commands and run targets stay accurate; add a target for anything new
          that must run (an "infra" target with `docker compose up <service>` /
          `docker compose stop <service>` for a new container; a "worker" target for a new
          background process).
        - `docker-compose.yml` — supporting services only (databases, caches, brokers, mail
          catchers). The app itself runs natively with its dev command so it hot-reloads;
          do not move it into a container.
        - `.env.example` — every variable the app reads has a WORKING LOCAL DEFAULT here that
          matches the compose file (`postgres://app:app@localhost:5432/app`, not a
          placeholder). Orkka copies this file to `.env` when none exists, so a placeholder
          means a crash on first run. Never put real secrets in it.
        - The app must still boot with `orkka.json`'s commands on a fresh clone: setup →
          start → serves its first page (or answers its health endpoint) with no manual
          steps beyond Docker running. Migrations run on start or from a documented command.
        If the repository has no `orkka.json` yet, add one in this task — `stack`, `setup`,
        `check`, `build` and the `run.targets` that start the app as it is today — even when
        your change does not touch how it starts. If it has one without a `stack` or without
        `check`, add them in this task.
        """;
}
