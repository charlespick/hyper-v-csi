namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// A Hyper-V checkpoint this driver took, as much as anything needs to
/// reference it again to tag or destroy it. Not persisted anywhere - a
/// checkpoint is durable in the VM's own configuration, so this is re-derived
/// by <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/> on every call
/// that needs it rather than remembered across a restart.
/// </summary>
/// <param name="SettingsPath">
/// The CIM path of the checkpoint's <c>Msvm_VirtualSystemSettingData</c>,
/// which is what <c>DestroySnapshot</c> and a tagging
/// <c>ModifySystemSettings</c> both address it by.
/// </param>
/// <param name="ElementName">
/// This driver's identity string for the checkpoint - see
/// <see cref="IHyperVHostClient.CreateCheckpointAsync"/> - carried alongside
/// the path so a caller that just found this via
/// <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/> does not need a
/// second read to log or reason about which snapshot it belongs to.
/// </param>
/// <param name="Notes">
/// The JSON <c>SnapshotService.BuildCheckpointNotes</c> wrote onto the
/// checkpoint's <c>Notes</c> property alongside its identity, carried back
/// so a recovery path can recover a checkpoint's original (volumeId,
/// snapshotName, createdAtUtc) without depending on <see cref="ElementName"/>
/// being parseable, or un-truncated. Nullable for two reasons: a checkpoint
/// this driver did not tag at all has none, and a checkpoint tagged by an
/// older build of this driver, before this property existed, may not carry
/// one either.
/// </param>
public sealed record Checkpoint(string SettingsPath, string ElementName, string? Notes);
