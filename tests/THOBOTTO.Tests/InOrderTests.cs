using THOBOTTO.Voice.Hearing;

namespace THOBOTTO.Tests;

public class InOrderTests
{
    [Fact]
    public async Task One_keys_work_runs_in_order_and_another_keys_beside_it()
    {
        var queue = new InOrder();
        var first = new TaskCompletionSource();
        var ran = new List<string>();
        var a1 = queue.Run(1, async () => { await first.Task; ran.Add("a1"); });
        var a2 = queue.Run(1, () => { ran.Add("a2"); return Task.CompletedTask; });
        var b1 = queue.Run(2, () => { ran.Add("b1"); return Task.CompletedTask; });

        await b1.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["b1"], ran);
        first.SetResult();
        await Task.WhenAll(a1, a2).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["b1", "a1", "a2"], ran);
    }

    [Fact]
    public async Task A_failure_doesnt_stop_the_keys_later_work()
    {
        var queue = new InOrder();
        var failed = queue.Run(1, () => throw new InvalidOperationException("no"));
        var after = queue.Run(1, () => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(TimeSpan.FromSeconds(5)));
        await after.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(after.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Finished_keys_are_forgotten()
    {
        var queue = new InOrder();
        await queue.Run(7, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50 && queue.Pending > 0; i++)
            await Task.Delay(10);
        Assert.Equal(0, queue.Pending);
    }
}
