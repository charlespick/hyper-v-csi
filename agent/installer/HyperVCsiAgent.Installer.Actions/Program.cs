using System.Runtime.Versioning;
using HyperVCsiAgent.Installer.Actions;

// The whole exe only ever runs as an MSI custom action on the Windows host
// being installed onto - self-contained win-x64 publish, never anything else
// - so it is Windows-only end to end rather than needing per-call platform
// guards around GrantCertificateAccessCommand's certificate-store APIs.
[assembly: SupportedOSPlatform("windows")]

// Hosted by the MSI as an embedded Binary-stream custom action (see
// Product.wxs) rather than an in-process managed CA: WiX v5's managed CA
// hosting still needs a .NET Framework 4.x host, and HyperVCsiAgent.Core
// targets net10.0, so an in-process DLL could never reference it. Running as
// a plain exe - immediate, from the UI sequence, for wizard validation, and
// deferred/elevated, from the execute sequence, for the writes - sidesteps
// that entirely and lets every subcommand below share the exact validation
// and options types the agent itself binds and enforces at startup.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: HyperVCsiAgent.Installer.Actions <validate-cert|validate-thumbprints|grant-cert-access|write-config> [options]");
    return 1;
}

try
{
    return args[0] switch
    {
        "validate-cert" => ValidateCertificateCommand.Run(args[1..]),
        "validate-thumbprints" => ValidateThumbprintsCommand.Run(args[1..]),
        "grant-cert-access" => GrantCertificateAccessCommand.Run(args[1..]),
        "write-config" => WriteConfigCommand.Run(args[1..]),
        var unknown => Fail($"unknown command '{unknown}'"),
    };
}
catch (Exception ex)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
