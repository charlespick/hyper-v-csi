using System.Text.Json;

namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Body of POST /v1/jobs. Mirrors EnqueueJob in
/// csi-driver/internal/agentclient/client.go; the operation-specific fields stay
/// inside <see cref="Payload"/> so this envelope never has to grow a field per
/// RPC.
/// </summary>
public sealed class EnqueueJobRequest
{
    public string? OperationType { get; init; }

    /// <summary>Raw identifier from docs/rpc-surface-overview.md's "Idempotency Key" column.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// There is deliberately no target field. The resources a job must not
    /// interleave with are derived on this side, from this request's own
    /// payload - see <see cref="JobDispatcher"/> and <see cref="JobTargets"/>.
    /// A controller that named them could only repeat what it was told, and
    /// could not be held to spelling a VM ID the way this side does.
    /// </summary>
    public JsonElement Payload { get; init; }
}
