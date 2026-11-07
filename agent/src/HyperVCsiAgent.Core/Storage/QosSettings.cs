namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// A volume's Storage QoS, as its VolumeAttributesClass states it - the values a
/// Hyper-V <c>Dedicated</c> policy takes as-is. 0 means no reservation, or no
/// limit. A volume with no QoS at all is a null <see cref="QosSettings"/>, never
/// one whose values are all 0: the controller refuses an all-zero class, so
/// "no QoS" has exactly one spelling and leaves no policy behind.
/// </summary>
/// <param name="MinimumIops">The reservation, in normalized IOPS (<c>MinimumIOPS</c>). Best effort.</param>
/// <param name="MaximumIops">The limit, in normalized IOPS (<c>MaximumIOPS</c>).</param>
/// <param name="MaximumBandwidthBytesPerSecond">The bandwidth limit, in bytes per second (<c>MaximumIOBandwidth</c>).</param>
public sealed record QosSettings(ulong MinimumIops, ulong MaximumIops, ulong MaximumBandwidthBytesPerSecond)
{
    /// <summary>
    /// The same checks the controller makes, repeated at the agent's own edge
    /// so a payload that skipped them cannot leave an all-zero policy or one
    /// Hyper-V would refuse.
    /// </summary>
    /// <returns>Why the settings are unusable, or null when they are fine.</returns>
    public string? Problem()
    {
        if (MinimumIops == 0 && MaximumIops == 0 && MaximumBandwidthBytesPerSecond == 0)
        {
            return "every QoS value is 0; no QoS is a null qos, not an all-zero one";
        }

        if (MaximumIops > 0 && MinimumIops > MaximumIops)
        {
            return $"minimumIops {MinimumIops} exceeds maximumIops {MaximumIops}";
        }

        return null;
    }

    public override string ToString() =>
        $"minIOPS={MinimumIops} maxIOPS={MaximumIops} maxBandwidth={MaximumBandwidthBytesPerSecond}";
}
