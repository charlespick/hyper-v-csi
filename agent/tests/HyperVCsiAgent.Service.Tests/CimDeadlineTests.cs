using System.Runtime.Versioning;
using HyperVCsiAgent.Core.Tests;
using HyperVCsiAgent.Service.Cim;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// The one piece of the CIM-facing layer with no dependency on a live host, a
/// CIM session, or a cluster: a pure struct over <see cref="Environment.TickCount64"/>.
/// Everything else next to it (<c>CimHyperVHostClient</c>, <c>MsClusterService</c>,
/// <c>CimJobs</c>, <c>CimVirtualDiskManager</c>) needs a real Windows host to test
/// meaningfully; this does not, so there is no excuse for it to be untested.
///
/// <see cref="Microsoft.Management.Infrastructure.Options.CimOperationOptions"/>
/// comes from a Windows-specific package, so every test here is windows-only even
/// though nothing below actually touches CIM, a session, or the network.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CimDeadlineTests
{
    [WindowsOnlyFact]
    public void After_Remaining_IsPositiveAndNoMoreThanTheBudget()
    {
        var deadline = CimDeadline.After(TimeSpan.FromSeconds(30));

        var remaining = deadline.Remaining;

        Assert.True(remaining > TimeSpan.Zero, $"expected positive remaining time, got {remaining}");
        Assert.True(remaining <= TimeSpan.FromSeconds(30), $"expected at most the budget, got {remaining}");
    }

    [WindowsOnlyFact]
    public void Remaining_AfterTheBudgetElapses_IsExactlyZeroNeverNegative()
    {
        // A short budget and a real sleep past it - generous enough relative to
        // the budget that scheduler jitter on a loaded CI box won't flake it.
        var deadline = CimDeadline.After(TimeSpan.FromMilliseconds(30));

        Thread.Sleep(TimeSpan.FromMilliseconds(300));

        Assert.True(deadline.HasExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [WindowsOnlyFact]
    public void Options_DeadlineAlreadyExpired_ThrowsInsteadOfIssuingAnUntimedCall()
    {
        // A zero CimOperationOptions.Timeout means "no timeout" to CIM - the
        // opposite of what an expired deadline is supposed to produce, so this
        // has to throw rather than hand back options with Timeout == Zero.
        var deadline = CimDeadline.After(TimeSpan.FromMilliseconds(30));
        Thread.Sleep(TimeSpan.FromMilliseconds(300));

        var failure = Assert.Throws<TimeoutException>(
            () => deadline.Options("TestOperation", CancellationToken.None));

        Assert.Contains("TestOperation", failure.Message, StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public void Options_TokenAlreadyCancelled_ThrowsEvenWhenTheDeadlineIsStillLive()
    {
        var deadline = CimDeadline.After(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => deadline.Options("TestOperation", cts.Token));
    }

    [WindowsOnlyFact]
    public void Options_LiveDeadlineAndNonCancelledToken_ReturnsOptionsBoundToTheRemainingBudget()
    {
        var deadline = CimDeadline.After(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var options = deadline.Options("TestOperation", cts.Token);

        // Not exact equality: time passes between reading Remaining here and
        // inside Options, so this pins "close to" rather than "identical to".
        var delta = (options.Timeout - deadline.Remaining).Duration();
        Assert.True(delta < TimeSpan.FromSeconds(1), $"expected Timeout close to Remaining, delta was {delta}");
        Assert.Equal(cts.Token, options.CancellationToken);
    }
}
