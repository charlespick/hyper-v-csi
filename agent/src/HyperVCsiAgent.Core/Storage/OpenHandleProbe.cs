namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Whether anything at all has a VHDX open, readers included - the premise
/// <see cref="HostControl.IVhdxLocationService.LocateAsync"/> needs before its
/// answer means anything, and the test for whether a disk can be treated as
/// nobody's.
/// </summary>
/// <remarks>
/// An open that shares nothing, and deliberately not any narrower signal:
/// <list type="bullet">
/// <item>
/// A local CIM read failing with <see cref="VhdxInUseException"/> is not
/// enough. On the host running the VM, that read is answered by the very vmms
/// holding the file and succeeds - measured - so a disk attached to a VM on the
/// agent's own host would pass for unattached.
/// </item>
/// <item>
/// An open that shares reads is not enough either. The base of a differencing
/// chain is held open for reading only - which is how several children can
/// share one parent - so a sharing open succeeds against a source a checkpoint
/// has already frozen.
/// </item>
/// </list>
/// Denying all sharing is refused by every one of those holders, on every host.
/// The cost is that the probe itself briefly holds the file exclusively when
/// nothing else does.
/// </remarks>
public static class OpenHandleProbe
{
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult = unchecked((int)0x80070021);
    private const int UserMappedFileHResult = unchecked((int)0x800704C8);

    public static bool IsHeldOpen(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException ex) when (ex.HResult is SharingViolationHResult or LockViolationHResult or UserMappedFileHResult)
        {
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // Not this probe's to report: the caller's own next step opens or
            // reads the file for real, and says what each of these means.
            return false;
        }
    }
}
