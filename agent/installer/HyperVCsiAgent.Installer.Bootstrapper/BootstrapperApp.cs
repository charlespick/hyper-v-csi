using System;
using System.Threading;
using System.Windows;
using WixToolset.BootstrapperApplicationApi;

namespace HyperVCsiAgent.Installer.Bootstrapper;

internal sealed class BootstrapperApp : BootstrapperApplication
{
    private WizardViewModel? _viewModel;
    private bool _elevated;

    public int ExitCode { get; private set; }

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);

        _viewModel = new WizardViewModel(this.engine, args.Command);

        // Not for headless (/quiet, /passive, or Burn's own Display.Embedded
        // relaunch to uninstall an older related bundle - see
        // WizardViewModel.IsHeadless): those runs never reach the Certificate
        // page's "Generate new self-signed certificate..." button that needs
        // this, and Elevate() blocks on a UAC prompt that nobody is at the
        // keyboard to answer in an unattended run.
        if (!_viewModel.IsHeadless)
        {
            try
            {
                // Burn's own supported way to get the whole session running
                // elevated up front: it shows the UAC prompt itself and
                // reconnects to an elevated companion process, without which
                // the Certificate page's LocalMachine\My import (and anything
                // else privileged reached before Apply()) fails. See the
                // Bootstrapper csproj's remarks for why a requireAdministrator
                // manifest on this exe cannot do the same job.
                this.engine.Elevate(IntPtr.Zero);
                _elevated = true;
            }
            catch (Exception ex)
            {
                // The user declined the UAC prompt (or it otherwise failed) -
                // same exit code Cancel() uses for backing out of setup, since
                // nothing was ever planned/applied on this path either.
                this.engine.Log(LogLevel.Error, $"Elevation was declined or failed: {ex.Message}");
                _viewModel.CancelCommand.Execute(null);
            }
        }

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
        else if (_elevated)
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

        // else: elevation was declined in OnCreate, which already set
        // _viewModel.ExitCode via CancelCommand - fall straight through to
        // Quit below without ever showing the wizard.

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
