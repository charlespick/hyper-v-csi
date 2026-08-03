using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Tests;

public class InMemoryJobStoreTests
{
    [Fact]
    public async Task GetOrCreate_WhileRunning_ReturnsSameJobWithoutStartingASecondRun()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var first = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1", async (_, _) => await release.Task);
        await WaitForStatus(first, JobStatus.Running);

        var second = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1", (_, _) =>
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

        var first = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1", (_, _) => throw new InvalidOperationException("boom"));
        await WaitForTerminal(first);
        Assert.Equal(JobStatus.Failed, first.Status);

        var second = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1", (_, _) => Task.CompletedTask);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task GetOrCreate_JobFailureException_KeepsItsErrorCode()
    {
        var store = new InMemoryJobStore();

        var job = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1",
            (_, _) => throw JobFailureException.AlreadyExists("different size"));
        await WaitForTerminal(job);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(AgentErrorCodes.AlreadyExists, job.ErrorCode);
        Assert.Equal("different size", job.Error);
    }

    [Fact]
    public async Task GetOrCreate_UnclassifiedException_FailsAsInternal()
    {
        var store = new InMemoryJobStore();

        var job = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1",
            (_, _) => throw new InvalidOperationException("boom"));
        await WaitForTerminal(job);

        Assert.Equal(AgentErrorCodes.Internal, job.ErrorCode);
    }

    [Fact]
    public async Task GetOrCreate_SameIdempotencyKeyDifferentOperation_DoesNotCollide()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var create = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1", async (_, _) => await release.Task);
        await WaitForStatus(create, JobStatus.Running);

        var delete = store.GetOrCreate("pvc-1", "DeleteVolume", "vol-pvc-1", (_, _) => Task.CompletedTask);

        Assert.NotSame(create, delete);
        Assert.Equal("DeleteVolume", delete.OperationType);

        release.SetResult();
        await WaitForTerminal(delete);
    }

    [Fact]
    public async Task GetOrCreate_SameTarget_RunsJobsStrictlyInOrder()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var attach = store.GetOrCreate("pvc-1+node-a", "ControllerPublishVolume", "vm-node-a", async (_, _) => await release.Task);
        await WaitForStatus(attach, JobStatus.Running);

        var detach = store.GetOrCreate("pvc-1+node-a", "ControllerUnpublishVolume", "vm-node-a", (_, _) => Task.CompletedTask);

        await Task.Delay(50);
        Assert.Equal(JobStatus.Pending, detach.Status);

        release.SetResult();
        await WaitForTerminal(detach);
        Assert.Equal(JobStatus.Succeeded, detach.Status);
    }

    [Fact]
    public async Task GetOrCreate_DifferentTargets_RunConcurrently()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var blocked = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1", async (_, _) => await release.Task);
        await WaitForStatus(blocked, JobStatus.Running);

        var independent = store.GetOrCreate("pvc-2", "CreateVolume", "vol-pvc-2", (_, _) => Task.CompletedTask);
        await WaitForTerminal(independent);

        Assert.Equal(JobStatus.Succeeded, independent.Status);
        Assert.Equal(JobStatus.Running, blocked.Status);

        release.SetResult();
        await WaitForTerminal(blocked);
    }

    [Fact]
    public async Task Get_TerminalJobPastRetention_IsEvicted()
    {
        var clock = new FakeClock();
        var store = new InMemoryJobStore(clock);

        var job = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1", (_, _) => Task.CompletedTask);
        await WaitForTerminal(job);

        Assert.Same(job, store.Get(job.Id));

        clock.Advance(InMemoryJobStore.Retention + TimeSpan.FromSeconds(1));

        Assert.Null(store.Get(job.Id));
    }

    [Fact]
    public async Task Dispose_CancelsInFlightJobTokens()
    {
        var store = new InMemoryJobStore();
        var started = new TaskCompletionSource();

        var job = store.GetOrCreate("pvc-1", "CreateVolume", "vol-pvc-1", async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        await started.Task;
        store.Dispose();

        await WaitForTerminal(job);
        Assert.Equal(JobStatus.Failed, job.Status);
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

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
