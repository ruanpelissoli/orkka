namespace Orkka.Runners.Server;

using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Drains a runner's uploaded chunk stream into the run's channel, on behalf of the hubs'
/// <c>StreamRunChunks</c> methods.
///
/// A client-to-server SignalR stream normally ends by the consumer going away: the browser
/// closed the session (<see cref="RunnerLink.CloseRunChannel"/>) or the runner's connection
/// dropped (<see cref="RunnerLink.OnDisconnected"/>) — both complete the channel, and either
/// the next write throws <see cref="ChannelClosedException"/> or the stream itself faults
/// with the <see cref="OperationCanceledException"/> SignalR's stream tracker uses to tear
/// down a dead connection's streams. Letting those escape makes
/// <c>DefaultHubDispatcher</c> log "Failed to invoke hub method" at Error, so ordinary
/// disconnects — which the link already reports at Warning — end up being the entire
/// Error-level signal of a worker pod. They are swallowed here at Debug instead.
///
/// Everything else still propagates: a genuine fault inside the stream must reach the
/// dispatcher and be logged as an Error.
/// </summary>
public static class RunnerChunkPump
{
    public static async Task DrainAsync(
        string runId,
        IAsyncEnumerable<RunnerChunk> chunks,
        Channel<RunnerChunk> channel,
        CancellationToken connectionAborted,
        ILogger logger)
    {
        try
        {
            await foreach (var chunk in chunks)
                await channel.Writer.WriteAsync(chunk);
        }
        catch (ChannelClosedException)
        {
            // The consumer tore the run down (session closed / browser gone / runner
            // disconnected) while the runner was still uploading — residual chunks have
            // nowhere to go.
            logger.LogDebug("[Runner] Run {RunId}: consumer gone, dropping the rest of the upload", runId);
        }
        catch (OperationCanceledException) when (connectionAborted.IsCancellationRequested)
        {
            // The runner's connection went away mid-upload. SignalR aborts the connection
            // before completing its stream tracker, so the token is already signalled here.
            logger.LogDebug("[Runner] Run {RunId}: connection closed mid-upload", runId);
        }
        catch (HubException ex) when (ex.Message.Contains("canceled by client", StringComparison.OrdinalIgnoreCase))
        {
            // The runner cancelled its own upload: its run was told to stop (the turn's
            // consumer had already finished — the API closed the chat stream the moment the
            // reply was complete, the Worker cancelled the run, the runner obliged) and it
            // tore the stream down instead of completing it. Every chunk the run produced
            // was consumed before that; this is the normal end of a relayed chat turn, not
            // a fault (it was the Worker's only Error line for a successful turn, 2026-09-07).
            logger.LogDebug("[Runner] Run {RunId}: upload cancelled by the runner after the consumer finished", runId);
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }
}
