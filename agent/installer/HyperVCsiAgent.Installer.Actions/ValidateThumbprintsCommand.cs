using HyperVCsiAgent.Core.Security;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Run from the wizard's trusted-clients dialog: validates every pasted
/// client-certificate thumbprint with the exact same normalization and
/// well-formedness check <see cref="AuthenticationOptions"/> uses at agent
/// startup, so a pasted label or a stray character is caught in the wizard
/// instead of silently locking a caller out later.
/// </summary>
internal static class ValidateThumbprintsCommand
{
    public static int Run(string[] args)
    {
        var parsed = CommandLineArgs.Parse(args);
        var thumbprints = parsed.OptionalList("values");

        if (thumbprints.Length == 0)
        {
            Console.Error.WriteLine("at least one client certificate thumbprint is required");
            return 1;
        }

        var invalid = thumbprints
            .Select(ClientCertificateAuthenticator.Normalize)
            .Where(normalized => !ClientCertificateAuthenticator.IsWellFormed(normalized))
            .ToList();

        if (invalid.Count > 0)
        {
            Console.Error.WriteLine(
                $"not a SHA-1 thumbprint (expected {ClientCertificateAuthenticator.ThumbprintLength} hex characters): " +
                string.Join(", ", invalid));
            return 1;
        }

        return 0;
    }
}
