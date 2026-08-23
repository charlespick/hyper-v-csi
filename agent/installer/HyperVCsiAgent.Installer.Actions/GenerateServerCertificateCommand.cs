namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Deferred, unimpersonated custom action backing the Certificate page's
/// "will be generated" choice: the wizard itself only ever collects a
/// subject name (see HyperVCsiAgent.Installer.Bootstrapper's
/// GenerateCertificateWindow) - it runs asInvoker for its whole lifetime, so
/// importing into LocalMachine\My has to happen here instead, during the
/// MSI's own elevated execute sequence, the same "only mutate during the
/// already-elevated install/rollback window" rule every other write in this
/// project already follows.
/// </summary>
/// <remarks>
/// The resulting thumbprint is written to --thumbprint-file rather than
/// printed or returned as an MSI property - see EffectiveThumbprint for why.
/// Product.wxs schedules this unconditionally (Condition is just NOT
/// REMOVE) rather than only when a subject was actually supplied: an
/// operator picking an already-installed certificate this run must not let
/// a *previous* run's generated-certificate file linger at the fixed path
/// EffectiveThumbprint reads from, or that stale thumbprint would win over
/// the certificate actually selected this time. An empty --subject is
/// therefore "clear whatever is there," not an error.
/// </remarks>
internal static class GenerateServerCertificateCommand
{
    public static int Run(string[] args)
    {
        var parsed = CommandLineArgs.Parse(args);
        var subjectName = parsed.Optional("subject");
        var thumbprintFile = parsed.Require("thumbprint-file");

        if (string.IsNullOrEmpty(subjectName))
        {
            if (File.Exists(thumbprintFile))
            {
                File.Delete(thumbprintFile);
            }

            return 0;
        }

        var certificate = SelfSignedCertificateGenerator.CreateAndImport(subjectName);
        File.WriteAllText(thumbprintFile, certificate.Thumbprint);

        Console.WriteLine($"Generated certificate '{certificate.Subject}' (thumbprint {certificate.Thumbprint}).");
        return 0;
    }
}
