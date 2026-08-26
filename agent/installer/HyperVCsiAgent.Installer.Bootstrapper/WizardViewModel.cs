using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// Single view model behind every wizard page - see MainWindow.xaml for how
/// CurrentPageIndex picks which page's content is on screen. Pages bind
/// straight to this instance (inherited DataContext), so there is no
/// separate per-page view model.
/// </summary>
internal sealed class WizardViewModel : ViewModelBase
{
    // Same names as the MSI properties in HyperVCsiAgent.Installer/Product.wxs
    // - the bundle's own Variable declarations in Bundle.wxs reuse them too,
    // so a value set here round-trips to the chained MsiPackage untouched.
    private string _serviceAccount = "";
    private string _servicePassword = "";
    private string _servicePasswordConfirm = "";
    private string _csvVolumesRoot = "";
    private string _csvSnapshotsRoot = "";
    private string _tlsPort = "443";
    private string _storeName = "My";
    private string _storeLocation = "LocalMachine";
    private string _selectedCertificateKey = "";

    private int _currentPageIndex;
    private int _overallProgressPercentage;
    private string _statusText = "";
    private bool _isInstalling;
    private bool _installSucceeded;
    private bool _licenseAccepted;
    private bool _snapshotsEnabled;
    private bool _passwordLocked;
    private SetupMode _mode;
    private Dispatcher? _dispatcher;

    private readonly IEngine _engine;
    private readonly IBootstrapperCommand _command;

    // Highest DetectRelatedBundle Version seen so far, restricted to
    // RelationType.Upgrade (see OnDetectRelatedBundle) - null means no
    // related bundle has been reported at all, which ComputeSetupMode reads
    // as "nothing else under this UpgradeCode is registered". Finalized by
    // the time OnDetectComplete fires; DetectRelatedBundle only ever arrives
    // during Detect, never after.
    private string? _highestRelatedVersion;

    public WizardViewModel(IEngine engine, IBootstrapperCommand command)
    {
        _engine = engine;
        _command = command;
        IsUninstall = command.Action == LaunchAction.Uninstall;

        // Embedded is how Burn re-launches an OLDER related bundle to
        // uninstall it as part of applying a newer one (confirmed via the
        // Burn log: that relaunch carries "-burn.embedded" and
        // WixBundleUILevel=1) - without treating it as headless, that
        // relaunch showed its own full wizard window on top of the new
        // install's. None/Passive are the /quiet and /passive an operator
        // scripting a cluster-wide rollout would use. Full is the only mode
        // that gets the interactive wizard.
        IsHeadless = command.Display is Display.Embedded or Display.None or Display.Passive;

        // Only Install/Uninstall are knowable this early - Upgrade/Repair/
        // Downgrade all depend on DetectRelatedBundle and WixBundleInstalled,
        // neither of which is meaningful before the engine's own Detect()
        // runs (see OnDetectComplete, which replaces this with
        // ComputeSetupMode()'s real answer). Uninstall is set correctly
        // here and for good: ComputeSetupMode's own Uninstall branch depends
        // on nothing but command.Action, so it can never disagree with this
        // once Detect completes - including for Display.Embedded, whose
        // relaunch always carries Action == Uninstall (see IsHeadless's own
        // remarks above).
        Mode = IsUninstall ? SetupMode.Uninstall : SetupMode.Install;

        // bal:CommandLineVariables (Bundle.wxs) only wires command-line
        // NAME=value overrides into the built-in themed BA - confirmed by
        // running this bundle with a property override and finding it
        // still landed at its declared default. A custom BA has to apply
        // them itself, which is what makes SERVICEACCOUNT=... etc. on the
        // command line do anything at all, headless or not.
        ApplyCommandLineVariables();

        // Run once, up front, rather than each time the Prerequisites page
        // is shown: both checks are cheap and their answers do not change
        // over the lifetime of one wizard session.
        var hyperV = PrerequisiteChecks.CheckHyperVRole();
        var cluster = PrerequisiteChecks.CheckClusterMembership();
        PrerequisiteResults = [hyperV, cluster];
        IsClusterMember = cluster.Status == PrerequisiteStatus.Pass;

        Certificates = CertificateStoreLookup.ListCandidates();

        // Not for uninstall: none of these fields' pages are ever shown on
        // that path (see BeginUninstall's own remarks), so there is nothing
        // to pre-fill.
        if (!IsUninstall)
        {
            var existing = ExistingInstallationDetector.Detect();
            if (existing.ServiceAccount is { } account)
            {
                ServiceAccount = account;
                ServiceAccountCarriedOver = true;

                // A real account is already configured with SCM, so there is
                // a real password already in place too - SCM has no API that
                // gives it back, so the wizard cannot show or reuse it
                // directly, but it also does not need to: leaving
                // ServicePassword blank and PasswordLocked true is exactly
                // what PushVariablesToEngine and ServiceInstall's own
                // Password="[SERVICEPASSWORD]" (Product.wxs) need to leave
                // the account's password untouched. See PasswordLocked's own
                // remarks for the ServiceAccountPage side of this.
                PasswordLocked = true;
            }

            if (existing.CsvVolumesRoot is { } volumesRoot)
            {
                CsvVolumesRoot = volumesRoot;
                CsvVolumesRootCarriedOver = true;
            }

            if (existing.CsvSnapshotsRoot is { } snapshotsRoot)
            {
                CsvSnapshotsRoot = snapshotsRoot;
                CsvSnapshotsRootCarriedOver = true;
                SnapshotsEnabled = true;
            }

            // Only when the store actually has a matching candidate left -
            // Certificates was already populated above, and selecting a
            // thumbprint the DataGrid has no row for would just leave
            // nothing selected, so there is no reason to distinguish "not
            // configured" from "configured but the certificate is gone" here.
            if (existing.ServerCertThumbprint is { } serverCertThumbprint &&
                Certificates.FirstOrDefault(certificate =>
                    certificate.Thumbprint?.Equals(serverCertThumbprint, StringComparison.OrdinalIgnoreCase) == true) is { } existingCertificate)
            {
                SelectedCertificateKey = existingCertificate.Key;
                ServerCertificateCarriedOver = true;
            }

            foreach (var clientThumbprint in existing.ClientThumbprints)
            {
                ClientThumbprintList.Add(clientThumbprint);
            }
        }

        BackCommand = new RelayCommand(GoBack, () => CurrentPageIndex is > 0 and < ProgressPageIndex);
        NextCommand = new RelayCommand(GoNext, CanGoNext);
        InstallCommand = new RelayCommand(BeginInstall, () => CurrentPageIndex == ReadyToInstallPageIndex);
        UninstallCommand = new RelayCommand(BeginUninstall, () => CurrentPageIndex == UninstallConfirmPageIndex);
        UnlockPasswordCommand = new RelayCommand(() => PasswordLocked = false);
        OpenLogCommand = new RelayCommand(OpenLog);
        CancelCommand = new RelayCommand(Cancel);
        CloseCommand = new RelayCommand(() => Application.Current?.Shutdown());

        // Set after the field initializer's default (WelcomePageIndex) so an
        // uninstall launch skips straight past the whole install wizard
        // instead of replaying License/ServiceAccount/Storage/etc. pages
        // that have nothing to configure on the way out.
        if (IsUninstall)
        {
            CurrentPageIndex = UninstallConfirmPageIndex;
        }
    }

    public const int WelcomePageIndex = 0;
    public const int PrerequisitesPageIndex = 1;
    public const int ServiceAccountPageIndex = 2;
    public const int StoragePageIndex = 3;
    public const int CertificatePageIndex = 4;
    public const int TrustedClientsPageIndex = 5;
    public const int ClusteringPageIndex = 6;
    public const int ReadyToInstallPageIndex = 7;
    public const int ProgressPageIndex = 8;
    public const int FinishPageIndex = 9;

    // Outside the install wizard's own linear ordering (0-9) on purpose -
    // this is the only page an uninstall launch ever shows before jumping
    // straight to ProgressPageIndex, so it does not need to sit between any
    // of the install-only pages.
    public const int UninstallConfirmPageIndex = 10;

    // Same reasoning as UninstallConfirmPageIndex, and reached the same way:
    // OnDetectComplete jumps straight here once Mode resolves to Downgrade,
    // bypassing every page that leads to InstallCommand. This is what makes
    // "No Install button" structural rather than a Visibility binding
    // someone could miss on a page still reachable via Back/Next - there is
    // no path from here to ReadyToInstallPageIndex at all.
    public const int DowngradePageIndex = 11;

    /// <summary>Whether this launch is Burn's Uninstall action (Control Panel "Uninstall") rather than a fresh install.</summary>
    public bool IsUninstall { get; }

    /// <summary>
    /// What this run is actually doing - see <see cref="SetupMode"/>. Starts
    /// as <see cref="SetupMode.Install"/> or <see cref="SetupMode.Uninstall"/>
    /// (the only two knowable from the command line alone) and is replaced
    /// with <see cref="ComputeSetupMode"/>'s real answer once Detect
    /// completes. Every page heading, button label, and plan decision reads
    /// this instead of keeping its own opinion.
    /// </summary>
    public SetupMode Mode
    {
        get => _mode;
        private set
        {
            if (SetField(ref _mode, value))
            {
                RaisePropertyChanged(nameof(InstallButtonLabel));
                RaisePropertyChanged(nameof(ProgressTitle));
                RaisePropertyChanged(nameof(FinishTitle));
                RaisePropertyChanged(nameof(SetupSummary));
            }
        }
    }

    /// <summary>This build's own version, exactly as Burn assigned it to the bundle - not cached, since IEngine exposes no cheaper way to read it than a variable lookup and it never changes over the process lifetime anyway.</summary>
    public string BundleVersion => _engine.GetVariableString("WixBundleVersion");

    /// <summary>The highest related bundle Version Detect reported (see OnDetectRelatedBundle), or null if none was - i.e. what, if anything, this node already has under a different build. Drives the "from X to Y" wording in SetupSummary.</summary>
    public string? InstalledVersion => _highestRelatedVersion;

    /// <summary>True for /quiet, /passive, and Burn's own embedded relaunch of a related bundle - see the constructor's own remarks. No wizard page ever shows in this mode.</summary>
    public bool IsHeadless { get; }

    /// <summary>Populated once, in the constructor - see there for why.</summary>
    public IReadOnlyList<PrerequisiteCheckResult> PrerequisiteResults { get; }

    /// <summary>
    /// Whether Prerequisites found this host clustered - gates whether the
    /// Clustering page shows at all. GoNext/GoBack skip over
    /// ClusteringPageIndex entirely when this is false, since it is not a
    /// step to land on when it is not shown.
    /// </summary>
    public bool IsClusterMember { get; }

    /// <summary>Candidate server certificates for the Certificate page's table - see AddPendingCertificate for why this isn't just populated once.</summary>
    public IReadOnlyList<CertificateEntry> Certificates { get; private set; }

    // Set alongside each field's own pre-fill in the constructor, above -
    // ReadyToInstallPage binds these to a "(carried over)" suffix next to
    // the affected rows so an upgrade's pre-filled values read as detected,
    // not as silent defaults the operator might assume they typed
    // themselves (the installer upgrade validation checklist's check 3 is
    // a human confirming exactly this).
    public bool ServiceAccountCarriedOver { get; private set; }
    public bool CsvVolumesRootCarriedOver { get; private set; }
    public bool CsvSnapshotsRootCarriedOver { get; private set; }
    public bool ServerCertificateCarriedOver { get; private set; }

    public int ExitCode { get; private set; }

    public string ServiceAccount { get => _serviceAccount; set => SetField(ref _serviceAccount, value); }

    public string ServicePassword
    {
        get => _servicePassword;
        set
        {
            if (SetField(ref _servicePassword, value))
            {
                RaisePropertyChanged(nameof(PasswordsMatch));
                RaisePropertyChanged(nameof(ShowPasswordMismatch));
            }
        }
    }

    public string ServicePasswordConfirm
    {
        get => _servicePasswordConfirm;
        set
        {
            if (SetField(ref _servicePasswordConfirm, value))
            {
                RaisePropertyChanged(nameof(PasswordsMatch));
                RaisePropertyChanged(nameof(ShowPasswordMismatch));
            }
        }
    }

    public bool PasswordsMatch => ServicePassword == ServicePasswordConfirm;

    /// <summary>Only once the operator has actually started typing a confirmation - showing a mismatch warning before the second box has any text would just be noise.</summary>
    public bool ShowPasswordMismatch => !PasswordsMatch && ServicePasswordConfirm.Length > 0;

    /// <summary>
    /// True when an existing service account was detected for this node
    /// (see the constructor) and the operator has not clicked
    /// UnlockPasswordCommand yet. ServiceAccountPage shows a disabled,
    /// filled-looking password box and an "Update Password" button in this
    /// state instead of an empty box demanding new input - SCM has no API
    /// that gives the real password back, so there is nothing to actually
    /// pre-fill, only a state where retyping it is optional. CanGoNext
    /// allows leaving the page with ServicePassword still blank while this
    /// is true, and PushVariablesToEngine then sends that blank
    /// ServicePassword through unchanged - which is exactly what
    /// ServiceInstall's Password="[SERVICEPASSWORD]" (Product.wxs) needs to
    /// leave the account's password untouched rather than reset it.
    /// </summary>
    public bool PasswordLocked
    {
        get => _passwordLocked;
        private set
        {
            if (SetField(ref _passwordLocked, value))
            {
                RaisePropertyChanged(nameof(PasswordUnlocked));
            }
        }
    }

    /// <summary>Just <c>!PasswordLocked</c> - ServiceAccountPage's real, editable PasswordBox needs a positive condition to bind Visibility to, the same as every other BoolToVis binding in this wizard.</summary>
    public bool PasswordUnlocked => !PasswordLocked;

    public string CsvVolumesRoot { get => _csvVolumesRoot; set => SetField(ref _csvVolumesRoot, value); }
    public string CsvSnapshotsRoot { get => _csvSnapshotsRoot; set => SetField(ref _csvSnapshotsRoot, value); }
    public string TlsPort { get => _tlsPort; set => SetField(ref _tlsPort, value); }
    public string StoreName { get => _storeName; set => SetField(ref _storeName, value); }
    public string StoreLocation { get => _storeLocation; set => SetField(ref _storeLocation, value); }

    /// <summary>
    /// Backs the Certificate page's DataGrid selection - a row's
    /// <see cref="CertificateEntry.Key"/>, never its (possibly not-yet-real)
    /// Thumbprint. See <see cref="SelectedCertificate"/> for the row this
    /// resolves to and <see cref="CertificateEntry"/>'s own remarks for why
    /// Key exists as a separate thing at all.
    /// </summary>
    public string SelectedCertificateKey
    {
        get => _selectedCertificateKey;
        set
        {
            if (SetField(ref _selectedCertificateKey, value))
            {
                RaisePropertyChanged(nameof(SelectedCertificate));
                RaisePropertyChanged(nameof(CertificateSummary));
            }
        }
    }

    /// <summary>The currently selected row, or null before anything has been picked - resolved by key, not cached, since Certificates itself can grow (AddPendingCertificate) without the selection changing.</summary>
    public CertificateEntry? SelectedCertificate =>
        Certificates.FirstOrDefault(certificate => certificate.Key == SelectedCertificateKey);

    /// <summary>
    /// Ready to Install's own certificate line - the one place this whole
    /// page's choice has to read back unambiguously as "using this already-
    /// installed certificate" or "generating a new one for this subject",
    /// never just a bare thumbprint or subject name that could be either.
    /// </summary>
    public string CertificateSummary => SelectedCertificate switch
    {
        null => "(none selected)",
        { IsPending: true } pending => $"New self-signed certificate will be generated for '{pending.PendingSubjectName}'",
        { } existing => $"Existing certificate: {existing.DisplaySubject} ({existing.Thumbprint})",
    };

    /// <summary>
    /// Add/remove list backing the Trusted Clients page, replacing the old
    /// single semicolon-separated text field now that there is a real UI to
    /// build one out of proper rows. Joined back into a semicolon-separated
    /// string only where the MSI still expects one - see
    /// PushVariablesToEngine.
    /// </summary>
    public ObservableCollection<string> ClientThumbprintList { get; } = [];

    private bool _registerClusterResource;

    /// <summary>The Clustering page's checkbox - see OnApplyComplete for where checking it actually takes effect.</summary>
    public bool RegisterClusterResource { get => _registerClusterResource; set => SetField(ref _registerClusterResource, value); }

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        private set
        {
            if (SetField(ref _currentPageIndex, value))
            {
                RaisePropertyChanged(nameof(IsWelcomePage));
                RaisePropertyChanged(nameof(IsPrerequisitesPage));
                RaisePropertyChanged(nameof(IsServiceAccountPage));
                RaisePropertyChanged(nameof(IsStoragePage));
                RaisePropertyChanged(nameof(IsCertificatePage));
                RaisePropertyChanged(nameof(IsTrustedClientsPage));
                RaisePropertyChanged(nameof(IsClusteringPage));
                RaisePropertyChanged(nameof(IsReadyToInstallPage));
                RaisePropertyChanged(nameof(IsProgressPage));
                RaisePropertyChanged(nameof(IsFinishPage));
                RaisePropertyChanged(nameof(IsUninstallConfirmPage));
                RaisePropertyChanged(nameof(IsDowngradePage));
                RaisePropertyChanged(nameof(ShowBackButton));
                RaisePropertyChanged(nameof(ShowNextButton));
                RaisePropertyChanged(nameof(ShowInstallButton));
                RaisePropertyChanged(nameof(ShowUninstallButton));
                RaisePropertyChanged(nameof(ShowCancelButton));
                RaisePropertyChanged(nameof(ShowCloseButton));
            }
        }
    }

    public bool IsWelcomePage => CurrentPageIndex == WelcomePageIndex;
    public bool IsPrerequisitesPage => CurrentPageIndex == PrerequisitesPageIndex;
    public bool IsServiceAccountPage => CurrentPageIndex == ServiceAccountPageIndex;
    public bool IsStoragePage => CurrentPageIndex == StoragePageIndex;
    public bool IsCertificatePage => CurrentPageIndex == CertificatePageIndex;
    public bool IsTrustedClientsPage => CurrentPageIndex == TrustedClientsPageIndex;
    public bool IsClusteringPage => CurrentPageIndex == ClusteringPageIndex;
    public bool IsReadyToInstallPage => CurrentPageIndex == ReadyToInstallPageIndex;
    public bool IsProgressPage => CurrentPageIndex == ProgressPageIndex;
    public bool IsFinishPage => CurrentPageIndex == FinishPageIndex;
    public bool IsUninstallConfirmPage => CurrentPageIndex == UninstallConfirmPageIndex;
    public bool IsDowngradePage => CurrentPageIndex == DowngradePageIndex;

    public bool ShowBackButton => !IsUninstall && CurrentPageIndex is > WelcomePageIndex and < ProgressPageIndex;
    public bool ShowNextButton => !IsUninstall && CurrentPageIndex < ReadyToInstallPageIndex;
    public bool ShowInstallButton => !IsUninstall && CurrentPageIndex == ReadyToInstallPageIndex;
    public bool ShowUninstallButton => IsUninstall && CurrentPageIndex == UninstallConfirmPageIndex;
    // Not just "< ProgressPageIndex": UninstallConfirmPageIndex sits past
    // FinishPageIndex numerically, so Cancel needs its own not-yet-running,
    // not-finished-yet check that holds for both flows.
    public bool ShowCancelButton => CurrentPageIndex != ProgressPageIndex && CurrentPageIndex != FinishPageIndex;
    public bool ShowCloseButton => CurrentPageIndex == FinishPageIndex;

    public int OverallProgressPercentage { get => _overallProgressPercentage; private set => SetField(ref _overallProgressPercentage, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public bool IsInstalling { get => _isInstalling; private set => SetField(ref _isInstalling, value); }
    public bool InstallSucceeded
    {
        get => _installSucceeded;
        private set
        {
            if (SetField(ref _installSucceeded, value))
            {
                RaisePropertyChanged(nameof(FinishTitle));
                RaisePropertyChanged(nameof(ShowOpenLogButton));
            }
        }
    }

    public bool LicenseAccepted { get => _licenseAccepted; set => SetField(ref _licenseAccepted, value); }
    public bool SnapshotsEnabled { get => _snapshotsEnabled; set => SetField(ref _snapshotsEnabled, value); }

    /// <summary>
    /// ProgressPage's own heading - now Mode's case, not just IsUninstall's.
    /// Downgrade has no case of its own: it never reaches
    /// Plan, so ProgressPage is never shown for it in either display mode
    /// (interactive redirects to DowngradePageIndex; RunHeadlessPlan exits
    /// before calling Plan) - the "_" fallback below exists only so this
    /// switch is exhaustive, not because it is ever actually read.
    /// </summary>
    public string ProgressTitle => Mode switch
    {
        SetupMode.Upgrade => "Upgrading Hyper-V CSI Agent",
        SetupMode.Repair => "Repairing Hyper-V CSI Agent",
        SetupMode.Uninstall => "Uninstalling Hyper-V CSI Agent",
        _ => "Installing Hyper-V CSI Agent",
    };

    /// <summary>
    /// FinishPage's own heading. Depends on InstallSucceeded, not just Mode
    /// - this used to say "Setup Complete" even when StatusText right
    /// underneath it was reporting a failure, which reads as the installer
    /// contradicting itself; that guarantee (a failure never reads as
    /// success) is preserved unchanged, just re-keyed on Mode instead of
    /// IsUninstall. Downgrade has no case for the same reason as
    /// ProgressTitle - FinishPage is never reached for it - so it falls
    /// through to the same "Setup ..." wording as Install.
    /// </summary>
    public string FinishTitle => (Mode, InstallSucceeded) switch
    {
        (SetupMode.Uninstall, true) => "Uninstall Complete",
        (SetupMode.Uninstall, false) => "Uninstall Encountered an Error",
        (SetupMode.Upgrade, true) => "Upgrade Complete",
        (SetupMode.Upgrade, false) => "Upgrade Encountered an Error",
        (SetupMode.Repair, true) => "Repair Complete",
        (SetupMode.Repair, false) => "Repair Encountered an Error",
        (_, true) => "Setup Complete",
        (_, false) => "Setup Encountered an Error",
    };

    /// <summary>ReadyToInstallPage's action button text - MainWindow.xaml binds Content to this instead of the old hardcoded "Install". Uninstall uses its own separate UninstallCommand/button instead; Downgrade never reaches this page at all (see DowngradePageIndex), so "Install" is a safe, never-shown fallback for it.</summary>
    public string InstallButtonLabel => Mode switch
    {
        SetupMode.Repair => "Repair",
        SetupMode.Upgrade => "Upgrade",
        _ => "Install",
    };

    /// <summary>
    /// Welcome and Ready-to-Install's version line - shows both versions,
    /// e.g. "Upgrading Hyper-V CSI Agent from 0.1.0-beta.1 to 0.2.0 on this
    /// node." Null on Uninstall, whose pages never bind to this.
    /// </summary>
    public string? SetupSummary => Mode switch
    {
        SetupMode.Install => $"Installing Hyper-V CSI Agent {BundleVersion} on this node.",
        SetupMode.Upgrade => $"Upgrading Hyper-V CSI Agent from {InstalledVersion} to {BundleVersion} on this node.",
        SetupMode.Repair => $"Repairing Hyper-V CSI Agent {BundleVersion} on this node.",
        SetupMode.Downgrade =>
            $"Hyper-V CSI Agent {InstalledVersion} is already installed on this node, which is newer than this package ({BundleVersion}).",
        _ => null,
    };

    /// <summary>Only on failure - a successful run has nothing worth sending an operator digging through a log for.</summary>
    public bool ShowOpenLogButton => !InstallSucceeded;

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand UnlockPasswordCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CloseCommand { get; }

    public void AttachDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    private readonly ManualResetEventSlim _headlessCompleted = new(initialState: false);

    /// <summary>Blocks until headless Detect/Plan/Apply has finished - BootstrapperApp.Run's headless equivalent of joining the interactive path's WPF UI thread.</summary>
    public void WaitForHeadlessCompletion() => _headlessCompleted.Wait();

    private void SignalHeadlessCompletion() => _headlessCompleted.Set();

    private void ApplyCommandLineVariables()
    {
        foreach (var variable in _command.ParseCommandLine().Variables)
        {
            _engine.SetVariableString(variable.Key, variable.Value, formatted: false);
        }
    }

    /// <summary>
    /// Adds a "will be generated" placeholder row and selects it. Appends
    /// rather than replaces anything already in <see cref="Certificates"/>:
    /// an earlier pending row (or a real certificate) left unselected is
    /// simply inert - nothing has actually been generated yet for any
    /// pending row, so there is nothing to clean up, and the operator can
    /// still switch back to it later. That is also why this never touches
    /// the store itself - the certificate this describes does not exist
    /// until the MSI's own elevated execute sequence creates it (see
    /// HyperVCsiAgent.Installer.Actions' GenerateServerCertificateCommand).
    /// </summary>
    public void AddPendingCertificate(string subjectName)
    {
        var entry = CertificateEntry.Pending(subjectName);
        Certificates = [.. Certificates, entry];
        RaisePropertyChanged(nameof(Certificates));
        SelectedCertificateKey = entry.Key;
    }

    // WixBundleLog is set by the engine itself before Detect even begins
    // (confirmed in a real Burn log), so it is always populated by the time
    // FinishPage - and this button - can be showing. UseShellExecute=true
    // opens it with whatever this machine's default handler for .log files
    // is (Notepad, ordinarily), the same as double-clicking it in Explorer.
    private void OpenLog()
    {
        var logPath = _engine.GetVariableString("WixBundleLog");
        if (logPath.Length == 0)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText += $" (Could not open the log: {ex.Message})";
        }
    }

    private void Cancel()
    {
        if (ConfirmCancel())
        {
            Application.Current?.Shutdown();
        }
    }

    /// <summary>
    /// Shared by the wizard's own Cancel button and the window chrome's
    /// native close button (see ConfirmCloseFromWindowChrome) - both back
    /// out of setup the same way once confirmed, just triggered
    /// differently.
    /// </summary>
    private bool ConfirmCancel()
    {
        var message = IsUninstall
            ? "Are you sure you want to cancel? The Hyper-V CSI Agent will not be removed."
            : "Are you sure you want to cancel setup? The Hyper-V CSI Agent will not be installed.";
        var result = MessageBox.Show(message, "Cancel Setup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return false;
        }

        // Nothing was ever planned/applied on this path, so ApplyComplete
        // never runs to set ExitCode - without this it would default to 0
        // (success) even though setup did not run.
        const int ErrorInstallUserExit = 1602;
        ExitCode = ErrorInstallUserExit;
        return true;
    }

    /// <summary>
    /// Called from MainWindow's Closing handler for the native title-bar
    /// close button and Alt+F4, neither of which goes through CancelCommand
    /// - unlike the wizard's own Cancel button, window chrome has no
    /// Visibility binding to hide it during Progress. Blocks outright while
    /// Apply() is actually running: closing the UI does not stop Burn's own
    /// engine, which keeps installing regardless, so there is nothing safe
    /// to confirm there. Closes without asking once setup has already
    /// finished, same as clicking the visible Close button.
    /// </summary>
    public bool ConfirmCloseFromWindowChrome()
    {
        if (IsProgressPage)
        {
            MessageBox.Show(
                "Setup is currently running and cannot be closed from here.",
                "Setup In Progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return IsFinishPage || ConfirmCancel();
    }

    private void GoBack()
    {
        if (CurrentPageIndex <= WelcomePageIndex)
        {
            return;
        }

        var previous = CurrentPageIndex - 1;
        if (previous == ClusteringPageIndex && !IsClusterMember)
        {
            // Not a step to land on when this host is not a cluster
            // member - skip back over it to Trusted Clients.
            previous--;
        }

        CurrentPageIndex = previous;
    }

    private void GoNext()
    {
        if (!CanGoNext())
        {
            return;
        }

        // Informational only, and deliberately after the required-field
        // check above rather than folded into it - this never blocks
        // leaving the page, it just tells the operator what to expect.
        if (CurrentPageIndex == StoragePageIndex && SnapshotsEnabled)
        {
            var message = BlockCloneCheck.Describe(CsvVolumesRoot, CsvSnapshotsRoot);
            MessageBox.Show(message, "Storage Locations", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        var next = CurrentPageIndex + 1;
        if (next == ClusteringPageIndex && !IsClusterMember)
        {
            // Same skip as GoBack, forwards - straight on to Ready to
            // Install instead.
            next++;
        }

        CurrentPageIndex = next;
    }

    private bool CanGoNext()
    {
        if (CurrentPageIndex >= ReadyToInstallPageIndex)
        {
            return false;
        }

        return CurrentPageIndex switch
        {
            WelcomePageIndex => LicenseAccepted,
            ServiceAccountPageIndex => ServiceAccount.Length > 0 && (PasswordLocked || (ServicePassword.Length > 0 && PasswordsMatch)),
            StoragePageIndex => CsvVolumesRoot.Length > 0 && (!SnapshotsEnabled || CsvSnapshotsRoot.Length > 0),
            CertificatePageIndex => SelectedCertificate is not null,
            TrustedClientsPageIndex => ClientThumbprintList.Count > 0,
            _ => true,
        };
    }

    private void BeginInstall()
    {
        PushVariablesToEngine();
        CurrentPageIndex = ProgressPageIndex;
        IsInstalling = true;
        StatusText = "Installing...";

        // Repair in Repair mode, Install otherwise - this is also what fixes
        // the old oddity where re-running the same bundle planned Install
        // again, did nothing, and reported "Setup Complete". Never reached
        // in Downgrade mode: InstallCommand's own
        // CanExecute only allows firing from ReadyToInstallPageIndex, and
        // Downgrade is redirected to DowngradePageIndex before that page is
        // ever reachable (see OnDetectComplete) - so there is no path from
        // here into planning a downgrade at all.
        _engine.Plan(Mode == SetupMode.Repair ? LaunchAction.Repair : LaunchAction.Install);
    }

    private void BeginUninstall()
    {
        // No PushVariablesToEngine here: none of the wizard pages that
        // populate those fields are ever shown on this path, so they are
        // still their empty defaults - pushing them would blow away the
        // persisted values Burn already has from the original install for
        // no reason, and Uninstall does not need them anyway.
        CurrentPageIndex = ProgressPageIndex;
        IsInstalling = true;
        StatusText = "Uninstalling...";
        _engine.Plan(LaunchAction.Uninstall);
    }

    // Mirrors CanGoNext's per-page required-field checks - there is no
    // wizard to enforce them interactively in headless mode, so a missing
    // one has to be caught here instead of surfacing as a cryptic MSI
    // failure partway through Apply. TLSPORT/STORENAME/STORELOCATION are
    // never missing - Bundle.wxs declares real defaults for all three.
    private static readonly string[] RequiredInstallVariableNames =
    [
        "SERVICEACCOUNT", "CSVVOLUMESROOT", "CSVSNAPSHOTSROOT", "CLIENTTHUMBPRINTS",
    ];

    private List<string> GetMissingRequiredVariables()
    {
        var missing = RequiredInstallVariableNames.Where(name => _engine.GetVariableString(name).Length == 0).ToList();

        // SERVICEPASSWORD is the one exception: required unless SCM already
        // has this exact account registered, same as PasswordLocked's own
        // interactive-mode reasoning - and only worth checking at all once
        // an account is actually present.
        if (_engine.GetVariableString("SERVICEACCOUNT").Length > 0 && !PasswordLocked && _engine.GetVariableString("SERVICEPASSWORD").Length == 0)
        {
            missing.Add("SERVICEPASSWORD");
        }

        // A cert either already exists (SERVERCERTTHUMBPRINT) or gets
        // generated fresh (GENERATECERTSUBJECT) - see PushVariablesToEngine
        // - so unlike every other name above, exactly one of a pair has to
        // be present rather than one fixed name.
        if (_engine.GetVariableString("SERVERCERTTHUMBPRINT").Length == 0 && _engine.GetVariableString("GENERATECERTSUBJECT").Length == 0)
        {
            missing.Add("SERVERCERTTHUMBPRINT or GENERATECERTSUBJECT");
        }

        return missing;
    }

    private void PushVariablesToEngine()
    {
        _engine.SetVariableString("SERVICEACCOUNT", ServiceAccount, formatted: false);
        _engine.SetVariableString("SERVICEPASSWORD", ServicePassword, formatted: false);
        _engine.SetVariableString("CSVVOLUMESROOT", CsvVolumesRoot, formatted: false);
        _engine.SetVariableString("CSVSNAPSHOTSROOT", CsvSnapshotsRoot, formatted: false);
        _engine.SetVariableString("TLSPORT", TlsPort, formatted: false);
        _engine.SetVariableString("STORENAME", StoreName, formatted: false);
        _engine.SetVariableString("STORELOCATION", StoreLocation, formatted: false);

        // Exactly one of these two is ever non-empty: a pending row has no
        // real thumbprint to send (nothing has been generated yet - see
        // AddPendingCertificate), so the MSI generates one itself from
        // GENERATECERTSUBJECT during its own elevated execute sequence
        // instead (Product.wxs' GenerateServerCertificate custom action).
        // An already-installed certificate needs no generation at all, so
        // GENERATECERTSUBJECT stays blank and every downstream custom
        // action keeps using SERVERCERTTHUMBPRINT exactly as before.
        _engine.SetVariableString("SERVERCERTTHUMBPRINT", SelectedCertificate?.Thumbprint ?? "", formatted: false);
        _engine.SetVariableString("GENERATECERTSUBJECT", SelectedCertificate?.PendingSubjectName ?? "", formatted: false);

        _engine.SetVariableString("CLIENTTHUMBPRINTS", string.Join(';', ClientThumbprintList), formatted: false);
    }

    private void RunOnUiThread(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>
    /// Called once per related bundle Detect finds under this bundle's
    /// UpgradeCode (BootstrapperApp wires this to IEngine's own
    /// DetectRelatedBundle event). Filters to RelationType.Upgrade -
    /// verified against the API surface that RelationType has no separate
    /// "Downgrade" value (None/Detect/Upgrade/Addon/Patch/DependentAddon/
    /// DependentPatch/Update/ChainPackage): Burn reports Upgrade for any
    /// other bundle sharing this UpgradeCode regardless of which direction
    /// the version difference runs, so direction is ours to work out by
    /// comparing Version, which ComputeSetupMode does once Detect finishes.
    /// </summary>
    public void OnDetectRelatedBundle(DetectRelatedBundleEventArgs e)
    {
        if (e.RelationType != RelationType.Upgrade)
        {
            return;
        }

        // More than one related bundle is possible in principle (e.g. a
        // botched prior upgrade left two registered under the same
        // UpgradeCode) - keeping the highest Version seen means a downgrade
        // against ANY of them still gets caught. Comparison is via IEngine, same as
        // everywhere else in this file - see ComputeSetupMode's remarks on
        // why there is exactly one comparer in this process.
        if (_highestRelatedVersion is null || _engine.CompareVersions(e.Version, _highestRelatedVersion) > 0)
        {
            _highestRelatedVersion = e.Version;
        }
    }

    /// <summary>
    /// Backstop only: this code refuses a downgrade BEFORE ever
    /// calling Plan (see ComputeSetupMode/OnDetectComplete/RunHeadlessPlan),
    /// so Burn itself reaching this event means that refusal did not fire -
    /// worth a log line, not worth silently overriding. e.Status is left at
    /// its recommended value on purpose: the entire point of retiring the
    /// MSI's own AllowDowngrades="yes" gate was to move enforcement here,
    /// not to remove it, so this handler never sets e.Status to force
    /// success.
    /// </summary>
    public void OnApplyDowngrade(ApplyDowngradeEventArgs e)
    {
        _engine.Log(LogLevel.Error,
            $"Burn refused to apply a downgrade (recommended HRESULT 0x{e.Recommendation:X8}). " +
            "This should already have been caught before Plan - see SetupMode.Downgrade.");
    }

    /// <summary>
    /// The single place SetupMode is decided. Uninstall is
    /// checked FIRST and unconditionally returns - this is the fix for the
    /// UPGRADINGPRODUCTCODE class of bug: Display.Embedded's relaunch (an
    /// OLDER bundle asked to uninstall itself while a NEWER one is mid-
    /// install) always carries Action == Uninstall, so it is structurally
    /// impossible for that relaunch to ever reach the version comparison
    /// below and refuse its own removal as a "downgrade". See
    /// IsHeadless's own remarks for how that relaunch is identified, and
    /// SetupMode.Downgrade's remarks for the incident this mirrors.
    /// <para>
    /// This short-circuit is the ONLY place an action is exempted from the
    /// downgrade comparison. Both refusal sites - OnDetectComplete's
    /// interactive redirect and RunHeadlessPlan's 1638 - act on the resulting
    /// Mode alone and carry no action check of their own, deliberately: two
    /// guards of different shapes needing to stay in agreement is what
    /// previously let `/repair /quiet` slip past the headless refusal.
    /// </para>
    /// <para>
    /// One thing to revisit if Bundle.wxs ever gains an &lt;Update&gt;
    /// element: Burn's self-update path relaunches through
    /// UpdateReplace/UpdateReplaceEmbedded rather than Uninstall, so such a
    /// relaunch would reach the comparison below and could refuse itself the
    /// same way the Embedded case would have. There is no &lt;Update&gt;
    /// element today, so those actions are unreachable.
    /// </para>
    /// </summary>
    private SetupMode ComputeSetupMode()
    {
        if (_command.Action == LaunchAction.Uninstall)
        {
            return SetupMode.Uninstall;
        }

        if (_highestRelatedVersion is { } related)
        {
            // IEngine.CompareVersions is the ONLY version comparison in this
            // codebase, by design: Burn decides relatedness with
            // verutil.cpp's own rules (including a case-insensitive
            // prerelease-label quirk), and a second, hand-rolled or
            // NuGet.Versioning-based comparer that disagreed with it would
            // let the BA and the engine reach different conclusions about
            // the same pair of versions. Do not add one.
            var comparison = _engine.CompareVersions(related, BundleVersion);
            if (comparison > 0)
            {
                return SetupMode.Downgrade;
            }

            if (comparison < 0)
            {
                return SetupMode.Upgrade;
            }

            // Equal Version under a different related bundle entry: since
            // WiX v3.8 (issue 4583) Burn upgrades a same-version
            // related bundle in place rather than reporting it as a second
            // installed product, so in practice this bundle is already the
            // one WixBundleInstalled reports below, not a separate "related"
            // one. If it is ever seen anyway, there is nothing destructive
            // about treating it the same as re-running this exact build.
            return SetupMode.Repair;
        }

        // No related bundle at all: either a clean node, or this exact
        // build re-run (WixBundleInstalled != 0) - the case that, before
        // this mode existed, silently replayed the full install wizard and
        // reported "Setup Complete" having done nothing.
        return _engine.GetVariableNumeric("WixBundleInstalled") != 0 ? SetupMode.Repair : SetupMode.Install;
    }

    public void OnDetectComplete(DetectCompleteEventArgs e)
    {
        if (IsHeadless)
        {
            if (e.Status < 0)
            {
                _engine.Log(LogLevel.Error, $"Detection failed (0x{e.Status:X8}).");
                ExitCode = e.Status;
                SignalHeadlessCompletion();
                return;
            }

            Mode = ComputeSetupMode();
            RunHeadlessPlan();
            return;
        }

        if (e.Status < 0)
        {
            RunOnUiThread(() => StatusText = $"Detection failed (0x{e.Status:X8}).");
            return;
        }

        var mode = ComputeSetupMode();
        RunOnUiThread(() =>
        {
            Mode = mode;

            // Structural, not a Visibility binding: redirect straight past
            // every page that leads to InstallCommand so there is no path
            // left to Plan a downgrade interactively at all ("No Install
            // button"). See DowngradePageIndex's own remarks.
            if (Mode == SetupMode.Downgrade)
            {
                CurrentPageIndex = DowngradePageIndex;
            }
        });
    }

    // No wizard page ever runs in headless mode, so this is BeginInstall/
    // BeginUninstall's equivalent: refuse a downgrade, validate the
    // variables an unattended install needs, then plan - straight from Detect
    // completing, instead of waiting on a button click that will never come.
    private void RunHeadlessPlan()
    {
        // Unconditional on Mode, deliberately NOT nested inside an
        // Action == Install check, and placed before both the
        // missing-variable validation and any call to Plan. The requirement
        // is that in Downgrade mode the BA must not call Plan at all - so
        // keying this off the mode alone is what
        // makes it match the interactive path in OnDetectComplete, which also
        // refuses on Mode alone. An earlier version of this scoped the check
        // to Action == Install and had a real hole: `bundle.exe /repair
        // /quiet` on a node carrying a newer bundle computes
        // SetupMode.Downgrade, skipped the whole block because the action was
        // Repair rather than Install, and fell through to Plan with no
        // refusal, no log line and no 1638.
        //
        // This is safe to leave unconditional precisely because the one
        // action that must never be refused - Burn's Display.Embedded
        // relaunch of an older bundle to uninstall itself - carries
        // Action == Uninstall, and ComputeSetupMode short-circuits that to
        // SetupMode.Uninstall on its first line, so it can never *be*
        // Downgrade to begin with. The exemption lives in exactly one place;
        // do not add a second copy of it here, because two guards of
        // different shapes needing to agree is what produced the hole above.
        //
        // 1638 is ERROR_PRODUCT_VERSION, the code msiexec itself returned for
        // a blocked downgrade before the MSI's AllowDowngrades="yes" moved
        // that enforcement here. Reusing it means existing runbooks and CM error
        // handling (Puppet/Ansible/DSC exec resources keying off this exit
        // code) keep working unchanged, and the headless path fails loudly
        // rather than silently no-opping.
        if (Mode == SetupMode.Downgrade)
        {
            const int ErrorProductVersion = 1638;
            _engine.Log(LogLevel.Error,
                $"Refusing to install Hyper-V CSI Agent {BundleVersion}: {InstalledVersion} is already installed on this node and is newer. Uninstall it first.");
            ExitCode = ErrorProductVersion;
            SignalHeadlessCompletion();
            return;
        }

        if (_command.Action == LaunchAction.Install)
        {
            var missing = GetMissingRequiredVariables();
            if (missing.Count > 0)
            {
                const int ErrorInvalidParameter = 87;
                _engine.Log(LogLevel.Error,
                    $"Missing required propert{(missing.Count == 1 ? "y" : "ies")} for an unattended install: {string.Join(", ", missing)}.");
                ExitCode = ErrorInvalidParameter;
                SignalHeadlessCompletion();
                return;
            }
        }

        // Repair in Repair mode, same as BeginInstall's interactive
        // equivalent - only ever substituted for an Install action; an
        // explicit /uninstall or /repair on the command line is left
        // exactly as the operator asked.
        var action = _command.Action == LaunchAction.Install && Mode == SetupMode.Repair
            ? LaunchAction.Repair
            : _command.Action;
        _engine.Plan(action);
    }

    public void OnPlanComplete(PlanCompleteEventArgs e)
    {
        if (e.Status < 0)
        {
            if (IsHeadless)
            {
                _engine.Log(LogLevel.Error, $"Planning failed (0x{e.Status:X8}).");
                ExitCode = e.Status;
                SignalHeadlessCompletion();
                return;
            }

            RunOnUiThread(() =>
            {
                IsInstalling = false;
                InstallSucceeded = false;
                ExitCode = e.Status;
                StatusText = $"Planning failed (0x{e.Status:X8}).";
                CurrentPageIndex = FinishPageIndex;
            });
            return;
        }

        // GetApplyParentHandle touches the Window's interop state in the
        // interactive case, which (like everything else on a
        // DispatcherObject) can only be read from the thread that owns it -
        // this callback runs on the engine's own thread, not the UI
        // dispatcher.
        RunOnUiThread(() => _engine.Apply(GetApplyParentHandle()));
    }

    // Re-keyed on Mode instead of IsUninstall so Upgrade/Repair get their
    // own wording too - Downgrade has no case of its own (falls through to
    // Install's) because Apply is never reached in that mode in the first
    // place (see ComputeSetupMode/OnDetectComplete/RunHeadlessPlan).
    private string GetOutcomeMessage(bool succeeded, int status) => (succeeded, Mode) switch
    {
        (true, SetupMode.Uninstall) => "Hyper-V CSI Agent was removed successfully.",
        (true, SetupMode.Upgrade) => "Hyper-V CSI Agent was upgraded successfully.",
        (true, SetupMode.Repair) => "Hyper-V CSI Agent was repaired successfully.",
        (true, _) => "Hyper-V CSI Agent was installed successfully.",
        (false, SetupMode.Uninstall) => $"Uninstall failed (0x{status:X8}). See the log for details.",
        (false, _) => $"Setup failed (0x{status:X8}). See the log for details.",
    };

    public void OnApplyComplete(ApplyCompleteEventArgs e)
    {
        if (IsHeadless)
        {
            InstallSucceeded = e.Status >= 0;
            ExitCode = e.Status;
            _engine.Log(InstallSucceeded ? LogLevel.Standard : LogLevel.Error, GetOutcomeMessage(InstallSucceeded, e.Status));

            // Cluster registration has no command-line equivalent yet -
            // only the interactive Clustering page sets
            // RegisterClusterResource, so this is always false here. Left
            // in rather than special-cased away, so adding that property
            // later does not also require touching this method.
            if (InstallSucceeded && RegisterClusterResource)
            {
                try
                {
                    ClusterResourceRegistrar.Register();
                    _engine.Log(LogLevel.Standard, "The agent was also registered as a clustered role.");
                }
                catch (Exception ex)
                {
                    _engine.Log(LogLevel.Standard, $"Registering the agent as a clustered role failed: {ex.Message}");
                }
            }

            SignalHeadlessCompletion();
            return;
        }

        RunOnUiThread(() =>
        {
            IsInstalling = false;
            InstallSucceeded = e.Status >= 0;
            ExitCode = e.Status;
            StatusText = GetOutcomeMessage(InstallSucceeded, e.Status);

            // Only after the service itself exists - a cluster resource
            // pointing at a service the MSI never installed would have
            // nothing to bring online. A registration failure here does not
            // change ExitCode/InstallSucceeded: the agent is installed
            // either way, and this is reported as its own, separate
            // outcome rather than turned into an overall setup failure.
            if (InstallSucceeded && RegisterClusterResource)
            {
                try
                {
                    ClusterResourceRegistrar.Register();
                    StatusText += " The agent was also registered as a clustered role.";
                }
                catch (Exception ex)
                {
                    StatusText += $" Registering the agent as a clustered role failed: {ex.Message}";
                }
            }

            CurrentPageIndex = FinishPageIndex;
        });
    }

    public void OnError(ErrorEventArgs e)
    {
        if (IsHeadless)
        {
            _engine.Log(LogLevel.Error, e.ErrorMessage);
        }
        else
        {
            RunOnUiThread(() => StatusText = e.ErrorMessage);
        }

        e.Result = Result.Abort;
    }

    public void OnExecuteMsiMessage(ExecuteMsiMessageEventArgs e) =>
        RunOnUiThread(() => StatusText = e.Message);

    public void OnExecuteProgress(ExecuteProgressEventArgs e) =>
        RunOnUiThread(() => OverallProgressPercentage = e.OverallPercentage);

    public void OnProgress(ProgressEventArgs e) =>
        RunOnUiThread(() => OverallProgressPercentage = e.OverallPercentage);

    // Burn's engine rejects a null hwndParent on Detect/Apply outright
    // ("BA passed NULL hwndParent to Apply") even when nothing is ever
    // going to be shown against it - confirmed by a real headless run
    // failing with exactly that error once this stopped creating a WPF
    // window at all. The desktop window's own handle is always valid and
    // never makes anything appear on screen, which is all a
    // Display.None/Embedded/Passive run needs.
    internal static IntPtr GetHeadlessWindowHandle() => NativeMethods.GetDesktopWindow();

    private IntPtr GetApplyParentHandle() =>
        IsHeadless
            ? GetHeadlessWindowHandle()
            : Application.Current?.MainWindow is { } window
                ? new System.Windows.Interop.WindowInteropHelper(window).Handle
                : IntPtr.Zero;

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetDesktopWindow();
    }
}
