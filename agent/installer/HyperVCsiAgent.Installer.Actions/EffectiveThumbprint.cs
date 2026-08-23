namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Resolves "the server certificate's thumbprint" for every command that
/// needs it, covering both a pre-existing certificate (the SERVERCERTTHUMBPRINT
/// MSI property, passed straight through as --thumbprint) and one
/// GenerateServerCertificateCommand just generated.
/// </summary>
/// <remarks>
/// A deferred custom action's CustomActionData is captured when MSI
/// *schedules* it - during the same immediate pass Product.wxs' Condition
/// attributes evaluate on - which happens before any deferred action in the
/// sequence has actually run. That means GenerateServerCertificateCommand
/// cannot hand its result to a later deferred command (GrantCertificateAccessCommand,
/// WriteConfigCommand) through an MSI property the way SetWriteConfigCmd1/2/3
/// build up WRITECONFIGCMD: by the time GenerateServerCertificateCommand's own
/// certificate actually exists, every other command's CustomActionData was
/// already captured, still holding the blank SERVERCERTTHUMBPRINT the
/// operator's "will be generated" choice left behind. It writes the real
/// thumbprint to a file instead (--thumbprint-file, a literal path with no
/// property substitution in it, so its value needs no such scheduling-order
/// care), and every consumer here prefers that file's content once it exists.
/// </remarks>
internal static class EffectiveThumbprint
{
    public static string Resolve(string? thumbprintArg, string? thumbprintFilePath)
    {
        if (!string.IsNullOrEmpty(thumbprintFilePath) && File.Exists(thumbprintFilePath))
        {
            var fromFile = File.ReadAllText(thumbprintFilePath).Trim();
            if (fromFile.Length > 0)
            {
                return fromFile;
            }
        }

        return thumbprintArg ?? "";
    }
}
