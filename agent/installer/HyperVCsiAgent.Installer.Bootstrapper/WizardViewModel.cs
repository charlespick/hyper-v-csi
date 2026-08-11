using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private Dispatcher? _dispatcher;

    private readonly IEngine _engine;
    private readonly IBootstrapperCommand _command;

    public WizardViewModel(IEngine engine, IBootstrapperCommand command)
    {
        _engine = engine;
        _command = command;

        // Run once, up front, rather than each time the Prerequisites page
        // is shown: both checks are cheap and their answers do not change
        // over the lifetime of one wizard session.
        var hyperV = PrerequisiteChecks.CheckHyperVRole();
        var cluster = PrerequisiteChecks.CheckClusterMembership();
        PrerequisiteResults = [hyperV, cluster];
        IsClusterMember = cluster.Status == PrerequisiteStatus.Pass;

        Certificates = CertificateStoreLookup.ListCandidates();

        BackCommand = new RelayCommand(GoBack, () => CurrentPageIndex is > 0 and < ProgressPageIndex);
        NextCommand = new RelayCommand(GoNext, CanGoNext);
        InstallCommand = new RelayCommand(BeginInstall, () => CurrentPageIndex == ReadyToInstallPageIndex);
        CancelCommand = new RelayCommand(Cancel);
        CloseCommand = new RelayCommand(() => Application.Current?.Shutdown());
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
    public string ServicePassword { get => _servicePassword; set => SetField(ref _servicePassword, value); }
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
                RaisePropertyChanged(nameof(ShowBackButton));
                RaisePropertyChanged(nameof(ShowNextButton));
                RaisePropertyChanged(nameof(ShowInstallButton));
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

    public bool ShowBackButton => CurrentPageIndex is > WelcomePageIndex and < ProgressPageIndex;
    public bool ShowNextButton => CurrentPageIndex < ReadyToInstallPageIndex;
    public bool ShowInstallButton => CurrentPageIndex == ReadyToInstallPageIndex;
    public bool ShowCancelButton => CurrentPageIndex < ProgressPageIndex;
    public bool ShowCloseButton => CurrentPageIndex == FinishPageIndex;

    public int OverallProgressPercentage { get => _overallProgressPercentage; private set => SetField(ref _overallProgressPercentage, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public bool IsInstalling { get => _isInstalling; private set => SetField(ref _isInstalling, value); }
    public bool InstallSucceeded { get => _installSucceeded; private set => SetField(ref _installSucceeded, value); }
    public bool LicenseAccepted { get => _licenseAccepted; set => SetField(ref _licenseAccepted, value); }
    public bool SnapshotsEnabled { get => _snapshotsEnabled; set => SetField(ref _snapshotsEnabled, value); }

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CloseCommand { get; }

    public void AttachDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

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
            ServiceAccountPageIndex => ServiceAccount.Length > 0 && ServicePassword.Length > 0,
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
        // Bare-bones wizard only supports a fresh install today - no
        // modify/repair/uninstall flow yet, so detection results beyond
        // "did it succeed" are not acted on.
        if (e.Status < 0)
        {
            RunOnUiThread(() => StatusText = $"Detection failed (0x{e.Status:X8}).");
        }
    }

    public void OnPlanComplete(PlanCompleteEventArgs e)
    {
        if (e.Status < 0)
        {
            RunOnUiThread(() =>
            {
                IsInstalling = false;
                ExitCode = e.Status;
                StatusText = $"Planning failed (0x{e.Status:X8}).";
                CurrentPageIndex = FinishPageIndex;
            });
            return;
        }

        // GetMainWindowHandle touches the Window's interop state, which
        // (like everything else on a DispatcherObject) can only be read
        // from the thread that owns it - this callback runs on the
        // engine's own thread, not the UI dispatcher.
        RunOnUiThread(() => _engine.Apply(GetMainWindowHandle()));
    }

    public void OnApplyComplete(ApplyCompleteEventArgs e)
    {
        RunOnUiThread(() =>
        {
            IsInstalling = false;
            InstallSucceeded = e.Status >= 0;
            ExitCode = e.Status;
            StatusText = InstallSucceeded
                ? "Hyper-V CSI Agent was installed successfully."
                : $"Setup failed (0x{e.Status:X8}). See the log for details.";

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
        RunOnUiThread(() => StatusText = e.ErrorMessage);
        e.Result = Result.Abort;
    }

    public void OnExecuteMsiMessage(ExecuteMsiMessageEventArgs e) =>
        RunOnUiThread(() => StatusText = e.Message);

    public void OnExecuteProgress(ExecuteProgressEventArgs e) =>
        RunOnUiThread(() => OverallProgressPercentage = e.OverallPercentage);

    public void OnProgress(ProgressEventArgs e) =>
        RunOnUiThread(() => OverallProgressPercentage = e.OverallPercentage);

    private static IntPtr GetMainWindowHandle() =>
        Application.Current?.MainWindow is { } window
            ? new System.Windows.Interop.WindowInteropHelper(window).Handle
            : IntPtr.Zero;
}
