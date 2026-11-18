# ih0.Claude.Agent.SDK — vendored

This directory is a **vendored copy** of the `ih0.Claude.Agent.SDK` NuGet package (Claude Agent
SDK for .NET), carrying Orkka patches the published package does not have.

| | |
|---|---|
| Upstream | https://github.com/jimmystridh/claude-agent-sdk-csharp |
| Version | 0.1.6 (tag `v0.1.6`, commit `1260639e95489cffa5435471e89082099da9aaa9`) |
| License | MIT (per the package's nuspec / `PackageLicenseExpression`) |
| Source path upstream | `src/ih0.Claude.Agent.SDK/` |

The assembly name, version (`0.1.6`) and namespaces are unchanged, so consumers compile exactly as
they did against the package; only the `PackageReference` became a `ProjectReference`
(`Orkka.AgentRuntime`, `Orkka.Modules.Execution`, `Orkka.Modules.Agents`, `Orkka.AppHost`).

## Why vendor instead of upgrading

0.1.6 is the latest published version and upstream `main` is one test-only commit past it, so
there is no release carrying the fix below. The bug kills a feature in production (the pipeline's
in-process `orkka` MCP tool server never connects for any run), so we own the source until an
upstream release includes the fix — at which point this directory should be deleted and the
`PackageReference`s restored.

## Local patches (search the source for `[orkka patch]`)

1. **`Internal/QueryHandler.cs` — `HandleMcpMessageAsync`: JSON-RPC notifications broke SDK MCP
   servers.** The Claude Code CLI drives SDK-hosted (in-process) MCP servers through
   `control_request`/`mcp_message`, and awaits a `control_response` for *every* message, including
   the `notifications/initialized` notification it sends right after `initialize`. Notifications
   carry no `id`; upstream captured `default(JsonElement)` as the message id and serialized it
   into the `mcp_response`. `System.Text.Json` throws a parameterless `InvalidOperationException`
   ("Operation is not valid due to the current state of the object") when writing a default
   `JsonElement`; the `catch` re-serialized the same default id and threw again; the exception
   escaped to `HandleControlRequestAsync`, which answered the CLI with an error
   `control_response` — and the CLI logged `Failed to connect SDK MCP server: Error: Operation is
   not valid due to the current state of the object` and marked the server `failed`. Net effect:
   every in-process tool server (pipeline `orkka` server, the agent-chat tool adapter) was
   unavailable for the whole run, with nothing logged host-side. The patch keeps the id as
   `JsonElement?` (null → JSON `null`, which the CLI's SDK-MCP transport accepts; the Python SDK
   does the same). Regression test: `tests/Orkka.Tests/ThirdParty/ClaudeAgentSdkMcpBridgeTests.cs`.

2. **`Internal/QueryHandler.cs` — `StreamInputAsync`: stdin was closed 60s into every hooked
   run, poisoning long runs with fake tool denials.** stdin doubles as the control-protocol
   response channel (hook callbacks, `canUseTool`, SDK-MCP tool calls are all answered over
   it). Upstream streamed the prompt, waited for the run's *first* `result` message capped at
   `CLAUDE_CODE_STREAM_CLOSE_TIMEOUT` (default 60s), and closed stdin on timeout — mid-run. A
   pipeline run's only `result` arrives at the very end, usually minutes in, so after the cap
   fired the CLI's next hook-matched tool call (Write/Edit/Read under Orkka's PreToolUse
   guards) could never get its hook response and was cancelled ~30ms after the `tool_use` with
   the CLI's generic *"The user doesn't want to take this action right now"* denial
   (`toolDenialKind: "cancelled"`), written into the session transcript. A later `--resume`
   replayed that fake refusal to the model as a human denial — agents concluded file creation
   was forbidden and parked tasks to ask (DEN-5, 2026-08-24). Bash escaped on user runners
   (no Bash hook registered there), which is what made it look intermittent. The patch waits
   for the actual first result with no timer, releases on cancellation/close *without* closing
   stdin (teardown owns the transport there), and also holds stdin open when only a
   `canUseTool` callback is registered. Regression test:
   `tests/Orkka.Tests/ThirdParty/ClaudeAgentSdkStreamInputTests.cs`.

3. **`Internal/Transport/SubprocessCliTransport.cs` — `CloseAsync`: kill before closing
   stdin.** Upstream closed stdin first and killed after; a CLI mid-tool-call can react to the
   EOF in that gap by writing the same "user doesn't want" cancellation into the session
   transcript before dying, poisoning the next `--resume` exactly as in patch 2. A killed
   process gets no last word; closing a dead process's stdin is harmless.

4. **`Internal/Transport/SubprocessCliTransport.cs` — `BuildCommand`: always pass
   `--setting-sources`.** Upstream passed the flag only for a non-empty list, so by default the CLI
   loaded the REPOSITORY's `.claude/settings.json` / `settings.local.json` — committed hooks
   (arbitrary commands outside every Orkka guard), `env` (e.g. an `ANTHROPIC_BASE_URL` that sends
   the developer's key elsewhere), MCP servers and permissions. Verified with Claude Code 2.1.326:
   a committed SessionStart hook ran by default and did not with `--setting-sources user`. The
   patch makes null mean `user` (the developer's own settings) and an empty list mean none.
   Regression test: `tests/Orkka.Tests/ThirdParty/ClaudeAgentSdkSettingSourcesTests.cs`.

5. **`Internal/Transport/SubprocessCliTransport.cs` — `CloseAsync` kills the whole tree;
   `ProcessStarted` announces every CLI it starts.** The CLI starts shells, MCP servers and test
   runners of its own; upstream's plain `Kill()` left them running, and when Orkka stopped with a
   run in flight the CLI itself outlived the process (R5, 2026-11-18: claude.exe plus its MCP
   servers survived `./orkka stop`). Close now uses `Kill(entireProcessTree: true)`, and the
   static `ProcessStarted` hook lets the runner book the process (`AgentProcesses`) so its
   shutdown can end any CLI whose teardown never ran. Regression test:
   `tests/Orkka.Tests/ThirdParty/ClaudeAgentSdkCloseKillsTreeTests.cs`.

## Project-file differences from upstream

`ih0.Claude.Agent.SDK.csproj` was rewritten for this repo: the `TargetFramework` (net10.0, upstream
inherits net8.0 from its `Directory.Build.props`), no packing/README items, no
`TreatWarningsAsErrors`, package versions aligned with the solution, `InternalsVisibleTo Orkka.Tests`.

## Re-syncing with upstream

1. `git clone --branch vX.Y.Z https://github.com/jimmystridh/claude-agent-sdk-csharp`
2. Replace every file here except `ih0.Claude.Agent.SDK.csproj` and this document with
   `src/ih0.Claude.Agent.SDK/` from the clone.
3. Re-apply every `[orkka patch]` that upstream has not absorbed, bump the table above, and run
   `dotnet test tests/Orkka.Tests --filter ClaudeAgentSdkMcpBridgeTests`.
