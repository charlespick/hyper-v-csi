using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HyperVCsiAgent.Installer.Package.Tests;

/// <summary>
/// A thin wrapper over the <c>WindowsInstaller.Installer</c> COM automation
/// object, opened read-only against one built .msi and queried by SQL.
/// </summary>
/// <remarks>
/// There is no PIA/NuGet package referenced for this - the type library this
/// object implements ships as part of Windows itself (msi.dll), and every
/// member is reached by late-bound <see cref="Type.InvokeMember(string,
/// BindingFlags, System.Reflection.Binder?, object?, object?[]?)"/> rather
/// than an early-bound interface. <c>OpenDatabase</c> is called with persist
/// mode <c>0</c> (<c>msiOpenDatabaseModeReadOnly</c>): this tier's entire
/// premise is "assert what got authored, install nothing", and read-only
/// additionally means running these tests takes no lock that could collide
/// with a concurrent build of the same .msi.
///
/// MSI's SQL dialect has no <c>LIKE</c>, so <see cref="Query"/> is
/// deliberately dumb: it runs whatever SQL it is given and returns every
/// row's columns as strings, leaving all filtering to the C# caller rather
/// than tempting anyone to reach for a clever query the engine cannot run.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MsiDatabase : IDisposable
{
    private readonly object _installer;
    private readonly object _database;

    public MsiDatabase(string msiPath)
    {
        var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer")
            ?? throw new InvalidOperationException(
                "The 'WindowsInstaller.Installer' COM automation object is not registered on this machine. " +
                "It ships with Windows itself (msi.dll) on every supported Windows version, so this normally " +
                "means the test is somehow running on a stripped-down or non-Windows environment despite " +
                "passing the [WindowsOnlyFact] gate.");

        _installer = Activator.CreateInstance(installerType)
            ?? throw new InvalidOperationException("Activator.CreateInstance returned null for WindowsInstaller.Installer.");

        try
        {
            _database = InvokeMethod(_installer, "OpenDatabase", msiPath, 0)
                ?? throw new InvalidOperationException($"OpenDatabase('{msiPath}') returned null.");
        }
        catch
        {
            Marshal.ReleaseComObject(_installer);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="sql"/> and returns every row as a string array of
    /// exactly <paramref name="columnCount"/> columns, in the order the
    /// <c>SELECT</c> list names them. A null/empty column comes back as
    /// <see cref="string.Empty"/> rather than <see langword="null"/>, since
    /// several MSI tables (e.g. <c>File.Version</c> for a file with no
    /// authored version) use an absent value to mean exactly that.
    /// </summary>
    public List<string[]> Query(string sql, int columnCount)
    {
        var rows = new List<string[]>();
        var view = InvokeMethod(_database, "OpenView", sql)
            ?? throw new InvalidOperationException($"OpenView returned null for: {sql}");

        try
        {
            InvokeMethod(view, "Execute", Array.Empty<object>());

            while (true)
            {
                var record = InvokeMethod(view, "Fetch", Array.Empty<object>());
                if (record is null)
                {
                    break;
                }

                try
                {
                    var row = new string[columnCount];
                    for (var column = 0; column < columnCount; column++)
                    {
                        row[column] = (string?)InvokeGet(record, "StringData", column + 1) ?? string.Empty;
                    }

                    rows.Add(row);
                }
                finally
                {
                    Marshal.ReleaseComObject(record);
                }
            }
        }
        finally
        {
            InvokeMethod(view, "Close", Array.Empty<object>());
            Marshal.ReleaseComObject(view);
        }

        return rows;
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(_database);
        Marshal.ReleaseComObject(_installer);
    }

    private static object? InvokeMethod(object target, string name, params object?[]? args)
        => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    private static object? InvokeGet(object target, string name, params object?[]? args)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args);
}
