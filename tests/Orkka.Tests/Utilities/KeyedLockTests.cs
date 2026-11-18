namespace Orkka.Tests.Utilities;

using FluentAssertions;
using Orkka.SharedKernel.Utilities;

public sealed class KeyedLockTests
{
    [Fact]
    public async Task SameKey_Serializes_DifferentKeys_DoNot()
    {
        var locks = new KeyedLock<string>();
        var inside = 0;
        var maxInside = 0;

        async Task Work(string key)
        {
            await using (await locks.AcquireAsync(key))
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                await Task.Delay(5);
                Interlocked.Decrement(ref inside);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => Work("org"))));
        maxInside.Should().Be(1, "one holder per key at a time");

        var a = await locks.AcquireAsync("a");
        var acquireB = locks.AcquireAsync("b");
        (await Task.WhenAny(acquireB, Task.Delay(2000))).Should().BeSameAs(acquireB, "another key is not blocked");
        await (await acquireB).DisposeAsync();
        await a.DisposeAsync();

        locks.ActiveKeys.Should().Be(0, "released keys leave the map");
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotLeakTheKey()
    {
        var locks = new KeyedLock<int>();
        var held = await locks.AcquireAsync(1);
        using var cts = new CancellationTokenSource(50);

        var wait = () => locks.AcquireAsync(1, cts.Token);
        await wait.Should().ThrowAsync<OperationCanceledException>();

        await held.DisposeAsync();
        locks.ActiveKeys.Should().Be(0);
        await (await locks.AcquireAsync(1)).DisposeAsync();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target))
               && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
