namespace Orkka.Runners.Server;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// One runner identity's live link: request/response ops (SignalR client results), fire-
/// and-forget sends, and the chunk channels of in-flight streamed runs. A link is a
/// STABLE handle — the underlying connection may drop and be replaced underneath it
/// (reconnect, runner restart) while consumers keep the same instance. Offline links
/// report a disconnected <see cref="Status"/> and throw
/// <see cref="RunnerUnavailableException"/> from <see cref="InvokeAsync{T}"/>/<see cref="SendAsync"/>.
/// </summary>
public interface IRunnerLink
{
    bool IsConnected { get; }

    (bool Connected, RunnerHello? Hello) Status { get; }

    /// <summary>
    /// Human-readable blocker when the connected runner cannot serve the given provider
    /// (CLI missing or subscription not authenticated per the last Hello). Null when
    /// ready, when the runner is offline (the send path already reports disconnection),
    /// or when the provider status is unknown.
    /// </summary>
    string? GetProviderBlocker(AiProvider provider);

    /// <summary>Request/response op on the runner (SignalR client result).
    /// <paramref name="opTimeout"/> overrides the default 5-minute ceiling for ops that
    /// legitimately run long (utility AI generations) — a timed-out generation bills its
    /// full spend and delivers nothing.</summary>
    Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken ct, TimeSpan? opTimeout = null);

    /// <summary>Fire-and-forget message to the runner (StartAgentRun/StartChatRun/CancelRun).</summary>
    Task SendAsync(string method, object? arg, CancellationToken ct);

    Channel<RunnerChunk> OpenRunChannel(string runId);

    void CloseRunChannel(string runId);

    bool TryGetRunChannel(string runId, out Channel<RunnerChunk> channel);

    /// <summary>Consumes a run's chunk stream, cancelling the runner-side run when the
    /// caller aborts.</summary>
    IAsyncEnumerable<RunnerChunk> ConsumeRunAsync(string runId, CancellationToken ct);
}

/// <summary>Host-supplied user-facing wording for link failures — the Local edition talks
/// about `./orkka`, a hosted edition about the user's connected runner.</summary>
public sealed record RunnerLinkMessages(
    string RunnerOffline,
    string DisconnectedMidRun,
    // How the user signs a vendor CLI in on the runner — the Local edition has a status panel
    // with a Connect button; the hosted edition points at Settings → My runner.
    string ProviderLoginRemedy = "Connect it from the runner status panel and retry.");

public sealed class RunnerUnavailableException(string message)
    : InvalidOperationException(message);

/// <summary>
/// The <see cref="IRunnerLink"/> implementation over a SignalR hub's clients: tracks the
/// runner's current connection (a second connection for the same identity replaces the
/// first) and the chunk channels of in-flight runs. The owning hub feeds it via
/// <see cref="SetRunner"/>/<see cref="OnDisconnected"/>.
/// </summary>
public sealed class RunnerLink(
    IHubClients clients,
    ILogger<RunnerLink> logger,
    RunnerLinkMessages messages) : IRunnerLink
{
    private readonly object _lock = new();
    private string? _connectionId;
    private RunnerHello? _hello;

    private readonly ConcurrentDictionary<string, Channel<RunnerChunk>> _runs = new();

    private static readonly TimeSpan OpTimeout = TimeSpan.FromMinutes(5);

    public bool IsConnected
    {
        get { lock (_lock) return _connectionId is not null; }
    }

    /// <summary>Whether <paramref name="connectionId"/> is this link's current connection and
    /// its Hello carried <paramref name="instanceId"/> — i.e. the request comes from the runner
    /// that replaced any earlier one.</summary>
    public bool IsActiveInstance(string connectionId, string? instanceId)
    {
        lock (_lock)
            return instanceId is not null && _connectionId == connectionId
                && _hello?.InstanceId == instanceId;
    }

    public (bool Connected, RunnerHello? Hello) Status
    {
        get { lock (_lock) return (_connectionId is not null, _hello); }
    }

    public void SetRunner(string connectionId, RunnerHello hello)
    {
        bool sameConnection;
        lock (_lock)
        {
            sameConnection = _connectionId == connectionId;
            if (_connectionId is not null && _connectionId != connectionId)
            {
                // Same InstanceId = the one runner reconnected (restart, network blip) —
                // routine. Different (or unknown, pre-InstanceId runner) = a SECOND runner
                // process may be alternating Hellos with the first: ops then land on
                // whichever spoke last, and their failures masquerade as "the runner never
                // received the op". Warn loudly enough to be found from that symptom.
                if (hello.InstanceId is not null && hello.InstanceId == _hello?.InstanceId)
                    logger.LogInformation("[Runner] Runner reconnected on {New} (was {Old})",
                        connectionId, _connectionId);
                else
                    logger.LogWarning(
                        "[Runner] A DIFFERENT runner process replaced the active connection " +
                        "({Old} -> {New}). If you did not just restart the runner, two " +
                        "Orkka runner processes are running on {Machine} and requests will " +
                        "randomly reach either one — stop the extra process.",
                        _connectionId, connectionId, hello.MachineName);
            }
            _connectionId = connectionId;
            _hello = hello;
        }

        // The runner re-Hellos every minute on the same connection to refresh credential
        // status — announce only real (re)connections, not the refreshes.
        logger.Log(sameConnection ? LogLevel.Debug : LogLevel.Information,
            "[Runner] Connected: {Machine} (protocol v{Version}, providers: {Providers})",
            hello.MachineName, hello.ProtocolVersion,
            string.Join(", ", hello.Providers.Select(p =>
                $"{p.Provider}={(p.Authenticated ? "ready" : p.CliFound ? "unauthenticated" : "missing")}")));
    }

    // Remedy wording assumes the Orkka Local runner (`git pull` + `./orkka start`, the runner
    // status panel) — revisit when a hosted edition surfaces per-user runners.
    public string? GetProviderBlocker(AiProvider provider)
    {
        RunnerHello? hello;
        lock (_lock) hello = _hello;

        var cliKey = RunnerProviders.FromAiProviderName(provider.ToString());
        var status = hello?.Providers.FirstOrDefault(p => p.Provider == cliKey);
        if (hello is null)
            return null;

        var display = ProviderDisplayNames.Of(provider);

        // A connected runner that doesn't report this provider at all predates it — the
        // run would silently fall back to claude on the runner side. Only the providers
        // added after the Providers array shipped can be missing this way, so the check
        // stays targeted rather than firing for an unrelated gap.
        if (status is null)
            return provider is AiProvider.Ollama or AiProvider.XAI or AiProvider.OpenCode
                ? $"This agent uses {display}, but the runner on {hello.MachineName} predates {display} support — update Orkka there (`git pull`, then `./orkka start`) and retry."
                : null;

        if (status.Authenticated)
            return null;

        // Detection-supplied remedies (Ollama: server down / codex CLI missing / no models)
        // beat the generic installed/logged-in wording.
        if (!string.IsNullOrWhiteSpace(status.Detail))
            return $"This agent uses {display}, but it isn't ready on the runner ({hello.MachineName}): {status.Detail}";

        var problem = status.CliFound
            ? $"the {cliKey} CLI is not logged in"
            : $"the {cliKey} CLI is not installed";
        var remedy = status.CliFound
            ? messages.ProviderLoginRemedy
            : $"Install the {cliKey} CLI on that machine, then: {messages.ProviderLoginRemedy}";
        return $"This agent uses {display}, but {problem} on the runner ({hello.MachineName}). {remedy}";
    }

    public void OnDisconnected(string connectionId)
    {
        lock (_lock)
        {
            if (_connectionId != connectionId)
                return;
            _connectionId = null;
            _hello = null;
        }

        // Only worth a warning when work is lost; a runner stopping cleanly is routine.
        logger.Log(_runs.IsEmpty ? LogLevel.Information : LogLevel.Warning,
            "[Runner] Disconnected — failing {Count} in-flight run(s)", _runs.Count);
        foreach (var (runId, channel) in _runs)
        {
            channel.Writer.TryWrite(new RunnerChunk("Error", messages.DisconnectedMidRun));
            channel.Writer.TryWrite(new RunnerChunk("Done", ""));
            channel.Writer.TryComplete();
            _runs.TryRemove(runId, out _);
        }
    }

    private string RequireConnection()
    {
        lock (_lock)
        {
            return _connectionId
                ?? throw new RunnerUnavailableException(messages.RunnerOffline);
        }
    }

    public async Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken ct, TimeSpan? opTimeout = null)
    {
        var connectionId = RequireConnection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(opTimeout ?? OpTimeout);

        var client = clients.Client(connectionId);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return args.Length switch
            {
                0 => await client.InvokeAsync<T>(method, timeout.Token),
                1 => await client.InvokeAsync<T>(method, args[0], timeout.Token),
                _ => throw new ArgumentException("At most one argument — wrap params in a record."),
            };
        }
        catch (Exception ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // SignalR reports an op-timeout as a bare "Invocation canceled by the server"
            // with no method or duration — name the op and how long it ran, and say what
            // the timeout means: the runner-side work (often a CLI generation) outran the
            // budget, it was NOT necessarily lost in transit.
            logger.LogWarning(
                "[Runner] {Method} op timed out after {Elapsed:F0}s (budget {Budget:F0}s) — " +
                "the runner-side operation outran the invoke timeout ({Error})",
                method, sw.Elapsed.TotalSeconds, (opTimeout ?? OpTimeout).TotalSeconds, ex.Message);
            // Reported as "no answer from the machine right now", which every caller already
            // handles: the relay endpoints turned the raw SignalR cancellation into an
            // unhandled 500 and an error log instead (prod, 2026-09-18 — a conflict check on a
            // busy laptop ran past its four-minute budget). The runner is fine; the op is not
            // coming back.
            throw new RunnerUnavailableException(
                $"Your machine did not answer {method} within {(opTimeout ?? OpTimeout).TotalSeconds:F0}s — "
                + "it may be busy. Try again in a moment.");
        }
        catch (IOException ex) when (!timeout.IsCancellationRequested)
        {
            // The runner's connection closed while the call was in flight. SignalR reports it
            // as a bare IOException ("Connection 'x' disconnected."), which every relay
            // endpoint surfaced as an unhandled 500 with a stack trace (prod, 2026-09-17: a
            // runner restarting mid-setup, twice in two minutes). It is the same situation as
            // no connection at all, and every caller already knows what to do with that.
            logger.LogWarning(
                "[Runner] {Method} lost the runner after {Elapsed:F0}s — the connection closed mid-call ({Error})",
                method, sw.Elapsed.TotalSeconds, ex.Message);
            throw new RunnerUnavailableException(messages.RunnerOffline);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            logger.LogInformation(
                "[Runner] {Method} op cancelled by the caller after {Elapsed:F0}s ({Error})",
                method, sw.Elapsed.TotalSeconds, ex.Message);
            throw;
        }
    }

    public async Task SendAsync(string method, object? arg, CancellationToken ct)
    {
        var connectionId = RequireConnection();
        await clients.Client(connectionId).SendAsync(method, arg, ct);
    }

    public Channel<RunnerChunk> OpenRunChannel(string runId)
    {
        var channel = Channel.CreateUnbounded<RunnerChunk>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        if (!_runs.TryAdd(runId, channel))
            throw new InvalidOperationException($"Run {runId} already registered.");
        return channel;
    }

    public void CloseRunChannel(string runId)
    {
        if (_runs.TryRemove(runId, out var channel))
            channel.Writer.TryComplete();
    }

    public bool TryGetRunChannel(string runId, out Channel<RunnerChunk> channel)
        => _runs.TryGetValue(runId, out channel!);

    public async IAsyncEnumerable<RunnerChunk> ConsumeRunAsync(
        string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!_runs.TryGetValue(runId, out var channel))
            yield break;

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            {
                yield return chunk;
                if (chunk.Type is "Done")
                    break;
            }
        }
        finally
        {
            if (ct.IsCancellationRequested && IsConnected)
            {
                try { await SendAsync(RunnerProtocol.CancelRun, runId, CancellationToken.None); }
                catch { /* runner may be gone — nothing to cancel */ }
            }
            CloseRunChannel(runId);
        }
    }
}
