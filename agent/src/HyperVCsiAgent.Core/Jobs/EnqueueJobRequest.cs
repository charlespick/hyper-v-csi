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

    /// <summary>Raw identifier from CSI Spec.md's "Idempotency Key" column.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>The resource whose jobs must not interleave. See IJobStore.</summary>
    public string? Target { get; init; }

    public JsonElement Payload { get; init; }
}
