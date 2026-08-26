namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// What this launch of the bootstrapper is actually doing to this node -
/// computed once, in <see cref="WizardViewModel.OnDetectComplete"/>, from
/// <see cref="WixToolset.BootstrapperApplicationApi.IBootstrapperCommand.Action"/>
/// and the related-bundle/registration state Detect reports. Every string
/// (button labels, page headings, the Welcome/Ready-to-Install summary line)
/// and every plan decision (<see cref="WizardViewModel.BeginInstall"/>,
/// <see cref="WizardViewModel.RunHeadlessPlan"/>) is driven from this one
/// value instead of each re-deriving its own opinion of what is happening.
/// </summary>
internal enum SetupMode
{
    /// <summary>No related bundle detected and this exact bundle is not already registered - a clean node.</summary>
    Install,

    /// <summary>A related bundle is present whose <c>Version</c> compares lower than this one's - the ordinary "new build over old" case.</summary>
    Upgrade,

    /// <summary>
    /// No related bundle at a different version, but this exact bundle
    /// (<c>WixBundleVersion</c>) is already registered
    /// (<c>WixBundleInstalled != 0</c>) - re-running the same build. Before
    /// this mode existed, this case silently showed the full install wizard,
    /// planned <see cref="WixToolset.BootstrapperApplicationApi.LaunchAction.Install"/>
    /// again, and reported "Setup Complete" having done nothing.
    /// </summary>
    Repair,

    /// <summary>
    /// A related bundle is present whose <c>Version</c> compares HIGHER than
    /// this one's - installing an older build over a newer one. The MSI's own
    /// <c>DowngradeErrorMessage</c> gate is gone
    /// (<c>AllowDowngrades="yes"</c> forbids it), so refusing this is now
    /// entirely this code's job - see <see cref="WizardViewModel.RunHeadlessPlan"/>
    /// and the Downgrade branch of <see cref="WizardViewModel.OnDetectComplete"/>.
    /// Never reached when <c>Action == LaunchAction.Uninstall</c>, even for
    /// an older related bundle - see that check's own remarks for why
    /// (the <c>UPGRADINGPRODUCTCODE</c> class of bug).
    /// </summary>
    Downgrade,

    /// <summary>Burn's own Uninstall action (Control Panel "Uninstall", or Burn embedding an older bundle to remove it as part of applying a newer one).</summary>
    Uninstall,
}
