using System;
using System.Threading;
using System.Windows;
using WixToolset.BootstrapperApplicationApi;

namespace HyperVCsiAgent.Installer.Bootstrapper;

internal sealed class BootstrapperApp : BootstrapperApplication
{
    private WizardViewModel? _viewModel;

    public int ExitCode { get; private set; }

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);

        _viewModel = new WizardViewModel(this.engine, args.Command);

        // No engine.Elevate() here: it elevates Burn's own internal companion
        // process for engine-driven operations (Plan/Apply/package execution)
        // - see BurnEngine's CoreElevate - not this managed process's own
        // token, so it never actually helped the Certificate page's
        // LocalMachine\My import the way an earlier version of this method
        // assumed. That page no longer needs it at all: it only collects a
        // subject name now (GenerateCertificateWindow), and the actual
        // import happens later, elevated, during the MSI's own execute
        // sequence (HyperVCsiAgent.Installer.Actions' GenerateServerCertificateCommand)
        // - the same "mutate only during the already-elevated install
        // window" rule every other privileged write here already follows.
        // Apply() still elevates on its own when it actually needs to,
        // exactly as any per-machine Burn bundle does without any BA code
        // asking for it.
        // DetectRelatedBundle is how the view model learns
        // what else is registered under this bundle's UpgradeCode (feeding
        // SetupMode's Upgrade/Downgrade/Repair split), and ApplyDowngrade is
        // the backstop for the case where the engine reaches a downgrade
        // conclusion despite this code's own pre-Plan refusal (see
        // WizardViewModel.OnApplyDowngrade's own remarks on why that should
        // never actually fire).
        this.DetectRelatedBundle += (_, e) => _viewModel.OnDetectRelatedBundle(e);
        this.ApplyDowngrade += (_, e) => _viewModel.OnApplyDowngrade(e);
        this.DetectComplete += (_, e) => _viewModel.OnDetectComplete(e);
        this.PlanComplete += (_, e) => _viewModel.OnPlanComplete(e);
        this.ApplyComplete += (_, e) => _viewModel.OnApplyComplete(e);
        this.Error += (_, e) => _viewModel.OnError(e);
        this.ExecuteMsiMessage += (_, e) => _viewModel.OnExecuteMsiMessage(e);
        this.ExecuteProgress += (_, e) => _viewModel.OnExecuteProgress(e);
        this.Progress += (_, e) => _viewModel.OnProgress(e);
    }

    protected override void Run()
    {
        // Headless covers /quiet, /passive, and - critically - Burn
        // re-launching an old related bundle to uninstall it as part of an
        // upgrade (Display.Embedded): without this branch, that relaunch
        // showed its own full wizard window on top of the new install's
        // own window. See WizardViewModel.IsHeadless for the exact display
        // values this covers.
        if (_viewModel!.IsHeadless)
        {
            this.engine.Log(LogLevel.Standard, "Running headless (no UI).");

            // Detect(IntPtr.Zero) is rejected the same way Apply is - see
            // WizardViewModel.GetHeadlessWindowHandle's own remarks.
            this.engine.Detect(WizardViewModel.GetHeadlessWindowHandle());
            _viewModel.WaitForHeadlessCompletion();
        }
        else
        {
            this.engine.Log(LogLevel.Standard, "Launching Hyper-V CSI Agent setup UI.");

            // This method runs on the MTA thread ManagedBootstrapperApplication.Run
            // set up (see Program.cs's own remarks on why Main isn't [STAThread]).
            // WPF needs a real STA thread of its own, so the whole UI lives on a
            // dedicated one and this method just blocks until it exits.
            var uiThread = new Thread(RunWpfApplication);
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            uiThread.Join();
        }

        this.ExitCode = _viewModel!.ExitCode;
        this.engine.Quit(this.ExitCode);
    }

    private void RunWpfApplication()
    {
        var app = new Application();
        var window = new MainWindow { DataContext = _viewModel };
        _viewModel!.AttachDispatcher(window.Dispatcher);
        app.MainWindow = window;

        // Detect runs asynchronously and its DetectComplete callback only
        // reaches WizardViewModel once this thread's dispatcher starts
        // pumping messages via app.Run below.
        this.engine.Detect();

        app.Run(window);
    }
}
