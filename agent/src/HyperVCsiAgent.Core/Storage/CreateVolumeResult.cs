namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Success payload of a CreateVolume job, surfaced as the job's <c>result</c>
/// and decoded by the Go controller into a CreateVolumeResponse.
/// </summary>
/// <param name="VolumeId">
/// The CSI volume ID. We deliberately use the requested volume name verbatim so
/// the VHDX path is computable from the ID alone, with no name-to-ID mapping to
/// keep (and lose) across an agent restart.
/// </param>
/// <param name="ActualSizeBytes">
/// What the disk was actually created with, which is what CSI wants reported
/// back rather than what was asked for.
/// </param>
/// <param name="AlreadyPresent">
/// True when the disk was already on the CSV and this call only confirmed it.
/// Lets the controller tell a genuine name collision apart from a disk it just
/// created, which matters when the size doesn't fit the request.
/// </param>
public sealed record CreateVolumeResult(string VolumeId, long ActualSizeBytes, bool AlreadyPresent);
