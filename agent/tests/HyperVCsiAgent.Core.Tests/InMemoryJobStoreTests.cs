using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Tests;

public class InMemoryJobStoreTests
{
    [Fact]
    public async Task GetOrCreate_WhileRunning_ReturnsSameJobWithoutStartingASecondRun()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var first = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], async (_, _) => await release.Task);
        await WaitForStatus(first, JobStatus.Running);

        var second = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], (_, _) =>
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

        var first = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], (_, _) => throw new InvalidOperationException("boom"));
        await WaitForTerminal(first);
        Assert.Equal(JobStatus.Failed, first.Status);

        var second = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], (_, _) => Task.CompletedTask);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task GetOrCreate_JobFailureException_KeepsItsErrorCode()
    {
        var store = new InMemoryJobStore();

        var job = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"],
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

        var job = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"],
            (_, _) => throw new InvalidOperationException("boom"));
        await WaitForTerminal(job);

        Assert.Equal(AgentErrorCodes.Internal, job.ErrorCode);
    }

    [Fact]
    public async Task GetOrCreate_SameIdempotencyKeyDifferentOperation_DoesNotCollide()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var create = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], async (_, _) => await release.Task);
        await WaitForStatus(create, JobStatus.Running);

        var delete = store.GetOrCreate("pvc-1", "DeleteVolume", ["vol-pvc-1"], (_, _) => Task.CompletedTask);

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

        var attach = store.GetOrCreate("pvc-1+node-a", "ControllerPublishVolume", ["vm-node-a"], async (_, _) => await release.Task);
        await WaitForStatus(attach, JobStatus.Running);

        var detach = store.GetOrCreate("pvc-1+node-a", "ControllerUnpublishVolume", ["vm-node-a"], (_, _) => Task.CompletedTask);

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

        var blocked = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], async (_, _) => await release.Task);
        await WaitForStatus(blocked, JobStatus.Running);

        var independent = store.GetOrCreate("pvc-2", "CreateVolume", ["vol-pvc-2"], (_, _) => Task.CompletedTask);
        await WaitForTerminal(independent);

        Assert.Equal(JobStatus.Succeeded, independent.Status);
        Assert.Equal(JobStatus.Running, blocked.Status);

        release.SetResult();
        await WaitForTerminal(blocked);
    }

    [Fact]
    public async Task GetOrCreate_MultipleTargets_BlocksALaterJobOnEitherOfThem()
    {
        // The property that makes a second target mean anything: a job holding
        // {vm, volume} has to be in the way of a job holding only the vm *and* of
        // a job holding only the volume. Holding one of the two and being
        // recorded as holding both is exactly the failure D10 describes.
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var copy = store.GetOrCreate("pvc-1~snap", "CopySnapshot", ["vm:node-a", "volume:pvc-1"],
            async (_, _) => await release.Task);
        await WaitForStatus(copy, JobStatus.Running);

        var attach = store.GetOrCreate("pvc-2+node-a", "AttachVolume", ["vm:node-a"], (_, _) => Task.CompletedTask);
        var expand = store.GetOrCreate("pvc-1", "ExpandVolume", ["volume:pvc-1"], (_, _) => Task.CompletedTask);

        await Task.Delay(50);
        Assert.Equal(JobStatus.Pending, attach.Status);
        Assert.Equal(JobStatus.Pending, expand.Status);

        release.SetResult();
        await WaitForTerminal(attach);
        await WaitForTerminal(expand);
        Assert.Equal(JobStatus.Succeeded, attach.Status);
        Assert.Equal(JobStatus.Succeeded, expand.Status);
    }

    [Fact]
    public async Task GetOrCreate_MultipleTargets_LeavesUnrelatedTargetsFree()
    {
        // The other half: a job holding two targets must not serialize the whole
        // agent. A copy freezing one VM has to leave every other VM attaching
        // normally, which is the outcome the vm: target is meant to produce.
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var copy = store.GetOrCreate("pvc-1~snap", "CopySnapshot", ["vm:node-a", "volume:pvc-1"],
            async (_, _) => await release.Task);
        await WaitForStatus(copy, JobStatus.Running);

        var elsewhere = store.GetOrCreate("pvc-9+node-b", "AttachVolume", ["vm:node-b"], (_, _) => Task.CompletedTask);
        await WaitForTerminal(elsewhere);

        Assert.Equal(JobStatus.Succeeded, elsewhere.Status);
        Assert.Equal(JobStatus.Running, copy.Status);

        release.SetResult();
        await WaitForTerminal(copy);
    }

    [Fact]
    public async Task GetOrCreate_OverlappingTargetSets_CannotDeadlock()
    {
        // The cycle a naive implementation invites: {A,B} waits on B's chain,
        // {B,C} waits on C's, {C,A} waits on A's. It cannot happen here because
        // every job only ever awaits tails installed strictly before its own -
        // see GetOrCreate's remarks - and this is the test that says so out loud.
        var store = new InMemoryJobStore();

        var ab = store.GetOrCreate("1", "Op", ["A", "B"], (_, _) => Task.CompletedTask);
        var bc = store.GetOrCreate("2", "Op", ["B", "C"], (_, _) => Task.CompletedTask);
        var ca = store.GetOrCreate("3", "Op", ["C", "A"], (_, _) => Task.CompletedTask);

        await WaitForTerminal(ab);
        await WaitForTerminal(bc);
        await WaitForTerminal(ca);

        Assert.Equal(JobStatus.Succeeded, ab.Status);
        Assert.Equal(JobStatus.Succeeded, bc.Status);
        Assert.Equal(JobStatus.Succeeded, ca.Status);
    }

    [Fact]
    public async Task GetOrCreate_MultipleTargets_ReleasesEachQueueIndependently()
    {
        // Pending bookkeeping is per target, so a job that took a place in two
        // queues has to give both back. Getting this wrong leaks a queue that
        // never reaches zero, and every later job on that target waits on a tail
        // belonging to a job that finished long ago.
        var store = new InMemoryJobStore();

        var both = store.GetOrCreate("1", "Op", ["A", "B"], (_, _) => Task.CompletedTask);
        await WaitForTerminal(both);

        // If either queue leaked, one of these would still be waiting on the
        // finished job's tail rather than running immediately.
        var onA = store.GetOrCreate("2", "Op", ["A"], (_, _) => Task.CompletedTask);
        var onB = store.GetOrCreate("3", "Op", ["B"], (_, _) => Task.CompletedTask);

        await WaitForTerminal(onA);
        await WaitForTerminal(onB);
        Assert.Equal(JobStatus.Succeeded, onA.Status);
        Assert.Equal(JobStatus.Succeeded, onB.Status);
    }

    [Fact]
    public async Task GetOrCreate_RepeatedTarget_IsCountedOnce()
    {
        // An expand whose stale node hint resolves to the same string as its
        // volume target is not a caller error worth rejecting - it is one
        // resource named twice. Counting it twice would leave Pending at 1 after
        // the job finished and wedge that target forever.
        var store = new InMemoryJobStore();

        var job = store.GetOrCreate("1", "Op", ["A", "A"], (_, _) => Task.CompletedTask);
        await WaitForTerminal(job);

        Assert.Equal(["A"], job.Targets);

        var next = store.GetOrCreate("2", "Op", ["A"], (_, _) => Task.CompletedTask);
        await WaitForTerminal(next);
        Assert.Equal(JobStatus.Succeeded, next.Status);
    }

    [Fact]
    public void GetOrCreate_NoTargets_IsRejected()
    {
        // Every job serializes against something. A job with no targets would run
        // concurrently with everything, including itself on a replay, which no
        // caller wants and no caller could ask for by accident except through a
        // bug worth surfacing here rather than at 3am.
        using var store = new InMemoryJobStore();

        Assert.Throws<ArgumentException>(() => store.GetOrCreate("1", "Op", [], (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task Get_PendingJob_ReportsTheRunningJobsTargetAndOperationType()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var copy = store.GetOrCreate("pvc-1~snap", "CopySnapshot", ["vm:node-a", "volume:pvc-1"],
            async (_, _) => await release.Task);
        await WaitForStatus(copy, JobStatus.Running);

        var attach = store.GetOrCreate("pvc-2+node-a", "AttachVolume", ["vm:node-a"], (_, _) => Task.CompletedTask);

        var polled = store.Get(attach.Id);
        Assert.NotNull(polled);
        Assert.NotNull(polled!.QueuedBehind);
        Assert.Equal("vm:node-a", polled.QueuedBehind!.Target);
        Assert.Equal("CopySnapshot", polled.QueuedBehind.OperationType);

        release.SetResult();
        await WaitForTerminal(attach);
    }

    [Fact]
    public async Task Get_RunningJob_ReportsNoQueuedBehind()
    {
        var store = new InMemoryJobStore();
        var release = new TaskCompletionSource();

        var job = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], async (_, _) => await release.Task);
        await WaitForStatus(job, JobStatus.Running);

        Assert.Null(store.Get(job.Id)!.QueuedBehind);

        release.SetResult();
        await WaitForTerminal(job);
    }

    [Fact]
    public async Task Get_TerminalJob_ReportsNoQueuedBehind()
    {
        var store = new InMemoryJobStore();

        var job = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], (_, _) => Task.CompletedTask);
        await WaitForTerminal(job);

        Assert.Null(store.Get(job.Id)!.QueuedBehind);
    }

    [Fact]
    public async Task Get_PendingJob_QueuedBehindReflectsWhicheverJobIsRunningNow()
    {
        // The property the read-time computation exists for: a value captured
        // once at enqueue would still name whatever was running back then,
        // long after it finished. It has to track reality as the head of the
        // queue changes underneath the still-Pending job.
        var store = new InMemoryJobStore();
        var releaseFirst = new TaskCompletionSource();
        var releaseSecond = new TaskCompletionSource();

        var first = store.GetOrCreate("pvc-1~snap-1", "CopySnapshot", ["vm:node-a"],
            async (_, _) => await releaseFirst.Task);
        await WaitForStatus(first, JobStatus.Running);

        var second = store.GetOrCreate("pvc-1~snap-2", "CopySnapshot", ["vm:node-a"],
            async (_, _) => await releaseSecond.Task);
        var attach = store.GetOrCreate("pvc-2+node-a", "AttachVolume", ["vm:node-a"], (_, _) => Task.CompletedTask);

        Assert.Equal("CopySnapshot", store.Get(attach.Id)!.QueuedBehind!.OperationType);

        releaseFirst.SetResult();
        await WaitForStatus(second, JobStatus.Running);

        // Still queued behind vm:node-a and still behind a CopySnapshot by
        // operation type, but the first copy is gone now - this has to be a
        // fresh read finding the second one, not a cached answer repeating
        // what the first read already said.
        var queuedBehind = store.Get(attach.Id)!.QueuedBehind;
        Assert.NotNull(queuedBehind);
        Assert.Equal("vm:node-a", queuedBehind!.Target);
        Assert.Equal("CopySnapshot", queuedBehind.OperationType);
        Assert.Equal(JobStatus.Running, second.Status);

        releaseSecond.SetResult();
        await WaitForTerminal(attach);
        Assert.Null(store.Get(attach.Id)!.QueuedBehind);
    }

    [Fact]
    public async Task Get_TerminalJobPastRetention_IsEvicted()
    {
        var clock = new FakeClock();
        var store = new InMemoryJobStore(clock);

        var job = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], (_, _) => Task.CompletedTask);
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

        var job = store.GetOrCreate("pvc-1", "CreateVolume", ["vol-pvc-1"], async (_, cancellationToken) =>
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
