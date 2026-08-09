using System.Text.Json;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Turns a POST /v1/jobs body into the delegate the job store will run and the
/// targets it has to serialize against. The payload is decoded here, before the
/// job is created, so a malformed request comes back as a 400 the controller can
/// see immediately rather than as a job that fails a moment later.
/// </summary>
/// <remarks>
/// Deciding the targets here rather than accepting them from the controller is
/// the second reason to decode this early: every field a target is built from -
/// the volume, the node, the snapshot name - is already in hand at this moment,
/// and the agent is the only party in a position to know which resources an
/// operation actually reaches. A controller naming its own targets can only
/// repeat what it was told to say, and cannot be held to spelling a VM ID the
/// same way this side does. See <see cref="JobTargets"/>.
/// </remarks>
public sealed class JobDispatcher(IVhdxService vhdxService, IAttachService attachService, ISnapshotService snapshotService)
{
    public const string CreateVolume = "CreateVolume";

    public const string DeleteVolume = "DeleteVolume";

    public const string ExpandVolume = "ExpandVolume";

    public const string VolumeExists = "VolumeExists";

    public const string AttachVolume = "AttachVolume";

    public const string DetachVolume = "DetachVolume";

    public const string CreateSnapshot = "CreateSnapshot";

    public const string DeleteSnapshot = "DeleteSnapshot";

    public const string ListSnapshots = "ListSnapshots";

    /// <exception cref="InvalidJobRequestException">
    /// The operation is unknown or its payload is unusable.
    /// </exception>
    public ResolvedJob Resolve(string operationType, JsonElement payload, JsonSerializerOptions jsonOptions)
    {
        switch (operationType)
        {
            case CreateVolume:
                var request = Decode<CreateVolumePayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    throw new InvalidJobRequestException("payload.name is required");
                }

                if (request.SizeBytes <= 0)
                {
                    throw new InvalidJobRequestException("payload.sizeBytes must be positive");
                }

                return new ResolvedJob(
                    [JobTargets.Volume(request.Name)],
                    async (job, cancellationToken) =>
                        job.Result = await vhdxService
                            .CreateAsync(request.Name, request.SizeBytes, request.SourceSnapshotId, cancellationToken)
                            .ConfigureAwait(false));

            case DeleteVolume:
                var deleteRequest = Decode<DeleteVolumePayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(deleteRequest.VolumeId))
                {
                    throw new InvalidJobRequestException("payload.volumeId is required");
                }

                // No job.Result: a deleted volume has nothing left to describe,
                // so the controller reads only the status.
                return new ResolvedJob(
                    [JobTargets.Volume(deleteRequest.VolumeId)],
                    (_, cancellationToken) => vhdxService.DeleteAsync(deleteRequest.VolumeId, cancellationToken));

            case ExpandVolume:
                var expandRequest = Decode<ExpandVolumePayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(expandRequest.VolumeId))
                {
                    throw new InvalidJobRequestException("payload.volumeId is required");
                }

                if (expandRequest.SizeBytes <= 0)
                {
                    throw new InvalidJobRequestException("payload.sizeBytes must be positive");
                }

                return new ResolvedJob(
                    [JobTargets.Volume(expandRequest.VolumeId)],
                    async (job, cancellationToken) =>
                        job.Result = await vhdxService
                            .ExpandAsync(expandRequest.VolumeId, expandRequest.SizeBytes, expandRequest.NodeId, cancellationToken)
                            .ConfigureAwait(false));

            case VolumeExists:
                var existsRequest = Decode<VolumeExistsPayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(existsRequest.VolumeId))
                {
                    throw new InvalidJobRequestException("payload.volumeId is required");
                }

                // No job.Result: the answer is the job's own outcome. Success
                // means the disk is there, NotFound means it is not, and there
                // is nothing else the controller asked about.
                return new ResolvedJob(
                    [JobTargets.Volume(existsRequest.VolumeId)],
                    (_, cancellationToken) => vhdxService.ConfirmExistsAsync(existsRequest.VolumeId, cancellationToken));

            case AttachVolume:
                var attachRequest = Decode<AttachVolumePayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(attachRequest.VolumeId))
                {
                    throw new InvalidJobRequestException("payload.volumeId is required");
                }

                if (string.IsNullOrWhiteSpace(attachRequest.NodeId))
                {
                    throw new InvalidJobRequestException("payload.nodeId is required");
                }

                return new ResolvedJob(
                    [JobTargets.Vm(attachRequest.NodeId)],
                    async (job, cancellationToken) =>
                        job.Result = await attachService
                            .AttachAsync(attachRequest.VolumeId, attachRequest.NodeId, cancellationToken)
                            .ConfigureAwait(false));

            case DetachVolume:
                var detachRequest = Decode<DetachVolumePayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(detachRequest.VolumeId))
                {
                    throw new InvalidJobRequestException("payload.volumeId is required");
                }

                if (string.IsNullOrWhiteSpace(detachRequest.NodeId))
                {
                    throw new InvalidJobRequestException("payload.nodeId is required");
                }

                // No job.Result: a volume that is no longer attached has nothing
                // left to describe, so the controller reads only the status.
                return new ResolvedJob(
                    [JobTargets.Vm(detachRequest.NodeId)],
                    (_, cancellationToken) =>
                        attachService.DetachAsync(detachRequest.VolumeId, detachRequest.NodeId, cancellationToken));

            case CreateSnapshot:
                var createSnapshotRequest = Decode<CreateSnapshotPayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(createSnapshotRequest.SourceVolumeId))
                {
                    throw new InvalidJobRequestException("payload.sourceVolumeId is required");
                }

                if (string.IsNullOrWhiteSpace(createSnapshotRequest.SnapshotName))
                {
                    throw new InvalidJobRequestException("payload.snapshotName is required");
                }

                // Carries a result even though the snapshot may not be finished:
                // readyToUse is part of that result, so "not done yet" is a
                // successful job with something to say, never a job left Running.
                return new ResolvedJob(
                    [JobTargets.SnapshotOf(createSnapshotRequest.SourceVolumeId, createSnapshotRequest.SnapshotName)],
                    async (job, cancellationToken) =>
                        job.Result = await snapshotService
                            .CreateAsync(
                                createSnapshotRequest.SourceVolumeId, createSnapshotRequest.SnapshotName,
                                createSnapshotRequest.NodeId, cancellationToken)
                            .ConfigureAwait(false));

            case DeleteSnapshot:
                var deleteSnapshotRequest = Decode<DeleteSnapshotPayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(deleteSnapshotRequest.SnapshotId))
                {
                    throw new InvalidJobRequestException("payload.snapshotId is required");
                }

                // No job.Result: a deleted snapshot has nothing left to describe,
                // exactly as for DeleteVolume.
                return new ResolvedJob(
                    [JobTargets.Snapshot(deleteSnapshotRequest.SnapshotId)],
                    (_, cancellationToken) =>
                        snapshotService.DeleteAsync(deleteSnapshotRequest.SnapshotId, cancellationToken));

            case ListSnapshots:
                // Every field is optional, mirroring CSI's own filters, so there
                // is nothing to require here - only the object shape Decode
                // already insists on. A missing payload field is a listing that
                // is simply not narrowed that way.
                var listSnapshotsRequest = Decode<ListSnapshotsPayload>(payload, jsonOptions);

                return new ResolvedJob(
                    [JobTargets.Snapshots],
                    async (job, cancellationToken) =>
                        job.Result = await snapshotService.ListAsync(
                                listSnapshotsRequest.SnapshotId,
                                listSnapshotsRequest.SourceVolumeId,
                                listSnapshotsRequest.StartingToken,
                                listSnapshotsRequest.MaxEntries,
                                cancellationToken)
                            .ConfigureAwait(false));

            // There is deliberately no case for SnapshotService.CopySnapshot,
            // and this is not an oversight to be tidied up later. The copy is
            // internal: it only ever starts as a consequence of a CreateSnapshot
            // that has already run the preconditions, and one enqueued directly
            // over POST /v1/jobs would skip every one of them - the free-space
            // check and the attached-source refusal included - and start a
            // multi-hour write to the CSV that nothing asked for. Falling
            // through to the rejection below is the intended behaviour.
            default:
                throw new InvalidJobRequestException($"unsupported operationType {operationType}");
        }
    }

    /// <summary>
    /// One decoded job request: what it will do, and what it must not run
    /// alongside while doing it.
    /// </summary>
    /// <remarks>
    /// The two travel together because they are decided from the same decoded
    /// payload and are wrong apart. A delegate paired with the wrong targets is
    /// the exact shape of D10 in issue #14 - work that reaches a VM while
    /// holding only the volume - and separating them into two lookups is how
    /// that mismatch gets to happen quietly.
    /// </remarks>
    public sealed record ResolvedJob(IReadOnlyCollection<string> Targets, Func<Job, CancellationToken, Task> Run);

    private static T Decode<T>(JsonElement payload, JsonSerializerOptions jsonOptions)
    {
        if (payload.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidJobRequestException("payload must be an object");
        }

        try
        {
            return payload.Deserialize<T>(jsonOptions)
                ?? throw new InvalidJobRequestException("payload must not be null");
        }
        catch (JsonException ex)
        {
            throw new InvalidJobRequestException($"payload is not valid for this operation: {ex.Message}");
        }
    }
}
