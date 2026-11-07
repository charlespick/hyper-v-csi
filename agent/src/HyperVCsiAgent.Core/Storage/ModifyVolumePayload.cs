using System.Text.Json.Serialization;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of a ModifyVolume job: the QoS the volume's new VolumeAttributesClass
/// says it should have.
/// </summary>
/// <remarks>
/// No node ID, like ExpandVolume's: which VM, if any, has the disk is the
/// agent's to work out from the path - see <see cref="HostControl.IVhdxLocationService"/>.
/// </remarks>
public sealed class ModifyVolumePayload
{
    public string? VolumeId { get; init; }

    /// <summary>
    /// The QoS to apply, or null to turn QoS off. Required to be present even
    /// though null is a legal value: a payload that merely forgot the field
    /// would otherwise read as "turn QoS off" and delete the volume's policy.
    /// </summary>
    [JsonRequired]
    public QosSettings? Qos { get; init; }
}
