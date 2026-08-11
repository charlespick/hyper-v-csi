namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Run from the wizard's certificate dialog as an immediate custom action:
/// confirms a certificate with the entered thumbprint actually exists in the
/// chosen store before the wizard lets the operator move on, rather than
/// letting a typo surface later as a TLS handshake failure with no
/// installer left open to explain it.
/// </summary>
internal static class ValidateCertificateCommand
{
    public static int Run(string[] args)
    {
        var parsed = CommandLineArgs.Parse(args);
        var thumbprint = parsed.Require("thumbprint");
        var storeName = parsed.Require("store-name");
        var storeLocation = parsed.Require("store-location");

        var certificate = CertificateLookup.Find(thumbprint, storeName, storeLocation);
        if (certificate is null)
        {
            Console.Error.WriteLine(
                $"No certificate with thumbprint '{thumbprint}' was found in {storeLocation}\\{storeName}. " +
                "Install the certificate on this host before continuing, or double-check the thumbprint.");
            return 1;
        }

        Console.WriteLine($"Found certificate: {certificate.Subject} (expires {certificate.NotAfter:u})");
        return 0;
    }
}
