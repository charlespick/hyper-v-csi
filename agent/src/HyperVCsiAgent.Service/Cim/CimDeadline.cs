using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure.Options;

namespace HyperVCsiAgent.Service.Cim;

/// <summary>
/// The deadline for one logical operation, carried across every CIM call that
/// operation makes.
/// </summary>
/// <remarks>
/// This type exists because a <see cref="CancellationToken"/> cannot enforce a
/// timeout against CIM, and the agent's design depends on one that can. Measured
/// against an unreachable host on this cluster: a query with no timeout returned
/// after 21.2s, and every mechanism System.Management offers was ignored -
/// <c>EnumerationOptions.Timeout</c> 21.0s, <c>ConnectionOptions.Timeout</c>
/// 21.1s, and <c>ManagementOperationObserver.Cancel()</c> from another thread
/// 21.1s. All of them waited out the RPC layer's own failure instead. Only
/// <c>CimOperationOptions.Timeout</c> bounded the call, returning at 3.0s for a
/// 3s budget.
///
/// So a token is cooperative only: it stops work that has not started and
/// unblocks polling loops, and it does nothing at all to a thread already inside
/// a blocked RPC. The timeout is what actually holds, which is why every call
/// site takes its options from here rather than passing <c>null</c>.
///
/// The budget is absolute rather than per-call because an operation like attach
/// makes several CIM calls: giving each the full budget would let a sequence of
/// individually-timely calls run for an unbounded total, which is the same
/// wedged queue by a slower route.
/// </remarks>
[SupportedOSPlatform("windows")]
public readonly struct CimDeadline
{
    private readonly long _expiresAt;

    private CimDeadline(long expiresAt) => _expiresAt = expiresAt;

    public static CimDeadline After(TimeSpan budget) =>
        new(Environment.TickCount64 + (long)budget.TotalMilliseconds);

    /// <summary>
    /// What is left of the budget. Never negative, and never zero: a call issued
    /// with a zero timeout is one CIM would treat as "no timeout", which is the
    /// opposite of what an expired deadline means.
    /// </summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = _expiresAt - Environment.TickCount64;
            return left <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(left);
        }
    }

    public bool HasExpired => Remaining == TimeSpan.Zero;

    /// <summary>
    /// Options for the next call, or a throw if there is no budget left to make
    /// it with. Throwing here rather than issuing an untimed call is the point
    /// of the type: the failure has to surface as this operation running out of
    /// time, not as one more unbounded call.
    /// </summary>
    public CimOperationOptions Options(string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var remaining = Remaining;
        if (remaining == TimeSpan.Zero)
        {
            throw new TimeoutException(
                $"the operation's time budget was exhausted before {operation} could be issued");
        }

        return new CimOperationOptions
        {
            Timeout = remaining,

            // Carried so that a caller-initiated cancellation still unwinds
            // anything that has not yet reached the RPC layer. It is not what
            // bounds the call - see the remarks on this type.
            CancellationToken = cancellationToken,
        };
    }
}
