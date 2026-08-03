using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Tests;

public class InMemoryJobStoreTests
{
    [Fact]
    public async Task GetOrCreate_WhileRunning_ReturnsSameJobWithoutStartingASecondRun()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var first = store.GetOrCreate("pvc-1", "CreateVolume", async (_, _) => await release.Task);
        await WaitForStatus(first, JobStatus.Running);

        var second = store.GetOrCreate("pvc-1", "CreateVolume", (_, _) =>
            throw new InvalidOperationException("must not run a second time while the first is in flight"));

        Assert.Same(first, second);

        release.SetResult();
        await WaitForTerminal(first);
        Assert.Equal(JobStatus.Succeeded, first.Status);
    }

    [Fact]
    public async Task GetOrCreate_AfterFailure_StartsAFreshJob()
    {
        var store = new InMemoryJobStore();

        var first = store.GetOrCreate("pvc-1", "CreateVolume", (_, _) => throw new InvalidOperationException("boom"));
        await WaitForTerminal(first);
        Assert.Equal(JobStatus.Failed, first.Status);

        var second = store.GetOrCreate("pvc-1", "CreateVolume", (_, _) => Task.CompletedTask);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task GetOrCreate_SameIdempotencyKeyDifferentOperation_DoesNotCollide()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var create = store.GetOrCreate("pvc-1", "CreateVolume", async (_, _) => await release.Task);
        await WaitForStatus(create, JobStatus.Running);

        var delete = store.GetOrCreate("pvc-1", "DeleteVolume", (_, _) => Task.CompletedTask);

        Assert.NotSame(create, delete);
        Assert.Equal("DeleteVolume", delete.OperationType);

        release.SetResult();
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var store = new InMemoryJobStore();

        Assert.Null(store.Get("does-not-exist"));
    }

    private static async Task WaitForStatus(Job job, JobStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (job.Status != status)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"job never reached {status}, stuck at {job.Status}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitForTerminal(Job job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (job.Status is JobStatus.Pending or JobStatus.Running)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"job never reached a terminal state, stuck at {job.Status}");
            }

            await Task.Delay(10);
        }
    }
}
