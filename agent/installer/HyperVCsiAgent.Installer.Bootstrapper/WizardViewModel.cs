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
    private string _serverCertThumbprint = "";

    private int _currentPageIndex;
    private int _overallProgressPercentage;
    private string _statusText = "";
    private bool _isInstalling;
    private bool _installSucceeded;
    private bool _licenseAccepted;
    private bool _snapshotsEnabled;
    private bool _passwordLocked;
    private Dispatcher? _dispatcher;

    private readonly IEngine _engine;
    private readonly IBootstrapperCommand _command;

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
            }

            if (existing.CsvSnapshotsRoot is { } snapshotsRoot)
            {
                CsvSnapshotsRoot = snapshotsRoot;
                SnapshotsEnabled = true;
            }

            // Only when the store actually has a matching candidate left -
            // Certificates was already populated above, and selecting a
            // thumbprint the DataGrid has no row for would just leave
            // nothing selected, so there is no reason to distinguish "not
            // configured" from "configured but the certificate is gone" here.
            if (existing.ServerCertThumbprint is { } serverCertThumbprint &&
                Certificates.Any(certificate => certificate.Thumbprint.Equals(serverCertThumbprint, StringComparison.OrdinalIgnoreCase)))
            {
                ServerCertThumbprint = serverCertThumbprint;
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

    /// <summary>Whether this launch is Burn's Uninstall action (Control Panel "Uninstall") rather than a fresh install.</summary>
    public bool IsUninstall { get; }

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

    /// <summary>Candidate server certificates for the Certificate page's table - see RefreshCertificates for why this isn't just populated once.</summary>
    public IReadOnlyList<CertificateEntry> Certificates { get; private set; }

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
    public string ServerCertThumbprint { get => _serverCertThumbprint; set => SetField(ref _serverCertThumbprint, value); }

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
    public bool InstallSucceeded { get => _installSucceeded; private set => SetField(ref _installSucceeded, value); }
    public bool LicenseAccepted { get => _licenseAccepted; set => SetField(ref _licenseAccepted, value); }
    public bool SnapshotsEnabled { get => _snapshotsEnabled; set => SetField(ref _snapshotsEnabled, value); }

    /// <summary>ProgressPage's own heading - the one piece of UI text that has to read differently for the two flows.</summary>
    public string ProgressTitle => IsUninstall ? "Uninstalling Hyper-V CSI Agent" : "Installing Hyper-V CSI Agent";

    /// <summary>FinishPage's own heading - same reasoning as <see cref="ProgressTitle"/>.</summary>
    public string FinishTitle => IsUninstall ? "Uninstall Complete" : "Setup Complete";

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand UnlockPasswordCommand { get; }
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
    /// Re-reads the certificate store - called after generating a new
    /// self-signed certificate, since that adds an entry the constructor's
    /// one-time read of <see cref="Certificates"/> would otherwise never
    /// see.
    /// </summary>
    public void RefreshCertificates()
    {
        Certificates = CertificateStoreLookup.ListCandidates();
        RaisePropertyChanged(nameof(Certificates));
    }

    private void Cancel()
    {
        // Nothing was ever planned/applied on this path, so ApplyComplete
        // never runs to set ExitCode - without this it would default to 0
        // (success) even though setup did not run.
        const int ErrorInstallUserExit = 1602;
        ExitCode = ErrorInstallUserExit;
        Application.Current?.Shutdown();
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
            CertificatePageIndex => ServerCertThumbprint.Length > 0,
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
        _engine.Plan(LaunchAction.Install);
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
        "SERVICEACCOUNT", "CSVVOLUMESROOT", "CSVSNAPSHOTSROOT", "SERVERCERTTHUMBPRINT", "CLIENTTHUMBPRINTS",
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
        _engine.SetVariableString("SERVERCERTTHUMBPRINT", ServerCertThumbprint, formatted: false);
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

            RunHeadlessPlan();
            return;
        }

        // Bare-bones wizard only supports fresh install and uninstall today
        // - no modify/repair flow - so detection results beyond "did it
        // succeed" are not acted on.
        if (e.Status < 0)
        {
            RunOnUiThread(() => StatusText = $"Detection failed (0x{e.Status:X8}).");
        }
    }

    // No wizard page ever runs in headless mode, so this is BeginInstall/
    // BeginUninstall's equivalent: validate (Install only) then plan
    // straight from Detect completing, instead of waiting on a button click
    // that will never come.
    private void RunHeadlessPlan()
    {
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

        _engine.Plan(_command.Action);
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

    private string GetOutcomeMessage(bool succeeded, int status) => (succeeded, IsUninstall) switch
    {
        (true, true) => "Hyper-V CSI Agent was removed successfully.",
        (true, false) => "Hyper-V CSI Agent was installed successfully.",
        (false, true) => $"Uninstall failed (0x{status:X8}). See the log for details.",
        (false, false) => $"Setup failed (0x{status:X8}). See the log for details.",
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
