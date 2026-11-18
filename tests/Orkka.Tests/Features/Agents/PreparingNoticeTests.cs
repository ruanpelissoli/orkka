namespace Orkka.Tests.Features.Agents;

using System.Diagnostics;
using FluentAssertions;
using Orkka.Modules.Agents.Features.AgentChat;

/// <summary>
/// The "Preparing the workspace…" notice exists for one case: a first clone of every repo onto
/// the user's machine, which is minutes of silence. Announced unconditionally it became the
/// first line of EVERY question the user asked ("I'm always seeing this message", 2026-09-08),
/// which is how a helpful notice turns into noise.
/// </summary>
public sealed class PreparingNoticeTests
{
    [Fact]
    public async Task A_fast_preparation_says_nothing()
    {
        var warm = Task.CompletedTask;

        var announce = await SendMessageEndpoint.OutlastsAsync(
            warm, TimeSpan.FromSeconds(3), CancellationToken.None);

        announce.Should().BeFalse();
    }

    [Fact]
    public async Task A_preparation_that_outlasts_the_window_is_announced_without_waiting_for_it()
    {
        var slow = Task.Delay(TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();

        var announce = await SendMessageEndpoint.OutlastsAsync(
            slow, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        announce.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the notice goes out while preparation is still running — it must not await it");
    }

    [Fact]
    public async Task A_preparation_that_finishes_inside_the_window_says_nothing()
    {
        var quick = Task.Delay(TimeSpan.FromMilliseconds(20));

        var announce = await SendMessageEndpoint.OutlastsAsync(
            quick, TimeSpan.FromSeconds(5), CancellationToken.None);

        announce.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_preparation_is_left_for_its_caller_to_observe()
    {
        // The caller awaits the task afterwards and turns the failure into an error event;
        // this probe must not consume the exception (nor raise it as unobserved).
        var failed = Task.FromException(new InvalidOperationException("clone failed"));

        var announce = await SendMessageEndpoint.OutlastsAsync(
            failed, TimeSpan.FromSeconds(3), CancellationToken.None);

        announce.Should().BeFalse();
        var act = async () => await failed;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("clone failed");
    }
}
