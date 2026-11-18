namespace Orkka.Modules.Execution.Agents;

using System.Text.Json;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The per-run USD spend ceiling, enforced mid-run. The Agent SDK only reports usage on the
/// final result message — useless for stopping a run that is still spending — but every
/// PreToolUse hook call carries the CLI's transcript path, and the transcript records each
/// assistant message's usage as it lands. This guard tails that file (throttled, incremental)
/// and prices the run with the same <see cref="CostCalculator"/> billing uses.
///
/// A tripped ceiling is a SOFT stop, deliberately: killing the process would throw away
/// everything already paid for (the exact failure mode that got turn caps removed — see
/// PhaseBudgetsOptions). Instead every further tool call is refused with a hand-off
/// instruction, so the model writes where it left off and the run ends as a short result
/// instead of a corpse. It is a PAUSE, not a finish: the invoker flags the Done chunk
/// (<see cref="Exhausted"/>) and the orchestrator parks the task asking the requester whether
/// to continue — a half-done branch is never pushed forward into review as if complete.
///
/// Claude-only, like the other hooks: the codex/gemini/copilot CLIs have no hook mechanism.
/// Their runs stay bounded by wall-clock alone.
/// </summary>
public sealed class RunCostGuard(string model, decimal maxCostUsd, ILogger logger)
{
    /// <summary>How often the transcript is re-read. Between polls the run can overshoot by
    /// at most one interval's worth of spend, which is the price of not parsing a growing
    /// JSONL file on every single tool call.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();

    /// <summary>Cumulative usage per assistant message id — last write wins, because the CLI
    /// appends one line per content block and later lines carry the completed usage.</summary>
    private readonly Dictionary<string, (long In, long Out, long CacheRead, long CacheWrite)> _byMessage = new();

    private long _readOffset;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    private volatile bool _exhausted;
    private decimal _lastCost;

    /// <summary>True once the ceiling was reached: the run is ending on a spend checkpoint,
    /// not because the agent decided it was done.</summary>
    public bool Exhausted => _exhausted;

    /// <summary>Vendor cost at the last poll, USD.</summary>
    public decimal LastCostUsd => _lastCost;

    public Task<HookOutput> EvaluateToolUse(HookInput input, string? toolUseId, HookContext? context)
    {
        if (_exhausted)
            return Task.FromResult(DenyExhausted());

        if (input is not PreToolUseHookInput pre || string.IsNullOrWhiteSpace(pre.TranscriptPath))
            return Task.FromResult(AgentHookResponses.Allow());

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastPoll >= PollInterval)
            {
                _lastPoll = now;
                try
                {
                    _lastCost = PriceTranscript(pre.TranscriptPath);
                }
                catch (Exception ex)
                {
                    // An unreadable transcript is a slower ceiling, never a dead run.
                    logger.LogDebug(ex, "Run cost poll failed for {Transcript}", pre.TranscriptPath);
                }

                if (_lastCost >= maxCostUsd)
                {
                    _exhausted = true;
                    logger.LogWarning(
                        "Run spend ceiling reached: ${Cost:F2} of ${Ceiling:F2} ({Model}) — refusing further tool calls",
                        _lastCost, maxCostUsd, model);
                }
            }
        }

        return Task.FromResult(_exhausted ? DenyExhausted() : AgentHookResponses.Allow());
    }

    /// <summary>Worded as a budget event, not a restriction — the agent must hand off, not
    /// conclude its tools are broken (see RefusedToolNotice for why the wording matters) and
    /// not try to "finish" without tools, which is how a checkpoint turns into invented work.</summary>
    private static HookOutput DenyExhausted() => AgentHookResponses.Deny(
        "This run has reached its spend checkpoint — no further tool calls will be approved. "
        + "This is a budget pause, not a malfunction, and not a request to finish: do not try "
        + "to complete the remaining work without tools. Stop now and write a short hand-off as "
        + "your final message: what is done, what remains, and exactly where you left off. The "
        + "person who owns this task will be asked whether to continue; if they do, the next run "
        + "starts from your hand-off.");

    /// <summary>Base (vendor) cost of every assistant message recorded so far. Incremental:
    /// only bytes appended since the previous poll are parsed, and only up to the last
    /// complete line — a partially flushed tail is re-read next poll.</summary>
    private decimal PriceTranscript(string transcriptPath)
    {
        using var stream = new FileStream(
            transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length <= _readOffset)
            return _lastCost;

        stream.Position = _readOffset;
        var buffer = new byte[stream.Length - _readOffset];
        stream.ReadExactly(buffer);

        // '\n' is 0x0A in every UTF-8 sequence position that matters — safe to scan bytes.
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
        if (lastNewline < 0)
            return _lastCost;

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        _readOffset += lastNewline + 1;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal))
                continue;

            try
            {
                ParseAssistantUsage(line);
            }
            catch (JsonException)
            {
                // Foreign or malformed line — skip; the totals only ever undercount.
            }
        }

        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        foreach (var usage in _byMessage.Values)
        {
            input += usage.In;
            output += usage.Out;
            cacheRead += usage.CacheRead;
            cacheWrite += usage.CacheWrite;
        }

        var costBase = CostCalculator.Calculate(
            model,
            (int)Math.Min(input, int.MaxValue), (int)Math.Min(output, int.MaxValue),
            (int)Math.Min(cacheRead, int.MaxValue), (int)Math.Min(cacheWrite, int.MaxValue));
        return costBase;
    }

    private void ParseAssistantUsage(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant"
            || !root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("usage", out var usage))
            return;

        var id = message.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
            return;

        _byMessage[id] = (
            ReadLong(usage, "input_tokens"),
            ReadLong(usage, "output_tokens"),
            ReadLong(usage, "cache_read_input_tokens"),
            ReadLong(usage, "cache_creation_input_tokens"));
    }

    private static long ReadLong(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt64()
            : 0;
}
