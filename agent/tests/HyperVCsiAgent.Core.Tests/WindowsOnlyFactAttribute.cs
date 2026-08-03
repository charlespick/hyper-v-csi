namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// A fact that runs only on Windows, and reports as skipped anywhere else
/// rather than passing without having checked anything.
///
/// Most of this suite is deliberately platform-agnostic - the whole point of
/// the CIM seam is that the policy above it can be exercised on a developer's
/// machine. A few behaviours genuinely can't be: mandatory file locking has no
/// Unix equivalent, where an open file can be unlinked freely, so anything
/// resting on a sharing violation has to be marked as unverified off Windows
/// instead of quietly asserting nothing.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "requires Windows: Unix filesystems allow unlinking a file that is held open";
        }
    }
}
