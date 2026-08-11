using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// The Storage page's live, informational-only check on Next: same
/// FILE_SUPPORTS_BLOCK_REFCOUNTING signal, read the same way, that
/// WindowsDiskCopier checks at runtime for the agent's own snapshot copies
/// - see that class's own remarks for why these are P/Invokes rather than
/// managed APIs. Duplicated rather than shared: WindowsDiskCopier lives in
/// HyperVCsiAgent.Service, and pulling that project's ASP.NET Core
/// dependencies into this installer for two P/Invoke declarations is not a
/// trade worth making.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BlockCloneCheck
{
    private const uint FileSupportsBlockRefcounting = 0x08000000;
    private const int VolumePathBufferChars = 261;

    /// <summary>
    /// Describes whether snapshots on these two paths will get block
    /// cloning - never throws, since this is informational only and must
    /// not stop the wizard from moving on regardless of the answer.
    /// </summary>
    public static string Describe(string volumesPath, string snapshotsPath)
    {
        try
        {
            var volumesRoot = ResolveVolumeRoot(volumesPath);
            var snapshotsRoot = ResolveVolumeRoot(snapshotsPath);

            if (string.Equals(volumesRoot, snapshotsRoot, StringComparison.OrdinalIgnoreCase)
                && SupportsBlockCloning(volumesRoot))
            {
                return "Block cloning will be used for snapshots on this system.";
            }

            return "Block cloning will not be used for snapshots on this system. For best " +
                   "performance, put both directories on the same ReFS volume with a 64 KB " +
                   "allocation unit.";
        }
        catch (Win32Exception)
        {
            // Most commonly: one of the paths does not exist yet. Informational
            // check, so this is worth telling the operator but never worth
            // blocking Next over - the agent re-checks this itself at runtime.
            return "Could not determine block-cloning support for these paths - they may not " +
                   "exist yet. This does not affect installation.";
        }
    }

    private static string ResolveVolumeRoot(string path)
    {
        var buffer = new StringBuilder(VolumePathBufferChars);
        if (!GetVolumePathNameW(path, buffer, buffer.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not resolve the volume {path} lives on");
        }

        return buffer.ToString();
    }

    private static bool SupportsBlockCloning(string volumeRoot)
    {
        if (!GetVolumeInformationW(volumeRoot, null, 0, out _, out _, out var flags, null, 0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(), $"could not read the filesystem capabilities of {volumeRoot}");
        }

        return (flags & FileSupportsBlockRefcounting) != 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}
