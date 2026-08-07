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
public sealed record Checkpoint(string SettingsPath, string ElementName);
