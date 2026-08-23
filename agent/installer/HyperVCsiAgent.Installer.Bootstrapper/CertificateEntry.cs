using System;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// One row of the Certificate page's certificate table - either a real
/// certificate already sitting in the store, or a placeholder standing in
/// for one the MSI will generate and import itself once the install is
/// actually running elevated (see <see cref="PendingSubjectName"/>).
/// </summary>
/// <remarks>
/// <see cref="Key"/>, not <see cref="Thumbprint"/>, is what the DataGrid's
/// SelectedValuePath binds to: a pending row has no real thumbprint yet,
/// and even once one exists it must never double as the row identity, or
/// adding a second pending row (or one that happens to land on the same
/// subject name as an already-generated certificate from a previous run)
/// would collide with - and silently reselect - an unrelated row.
/// </remarks>
internal sealed record CertificateEntry
{
    public required string Key { get; init; }
    public required string DisplaySubject { get; init; }
    public required string DisplayThumbprint { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
    public string? Thumbprint { get; init; }
    public string? PendingSubjectName { get; init; }

    public bool IsPending => PendingSubjectName is not null;

    public static CertificateEntry FromStore(string subjectName, string thumbprint, DateTimeOffset notAfter) => new()
    {
        Key = thumbprint,
        DisplaySubject = subjectName,
        DisplayThumbprint = thumbprint,
        NotAfter = notAfter,
        Thumbprint = thumbprint,
    };

    public static CertificateEntry Pending(string subjectName) => new()
    {
        // A GUID, not the subject name itself: two pending rows can share a
        // subject (the operator queued the same host twice, or a name that
        // happens to match a real certificate's own subject text), and each
        // still needs a Key nothing else in the table can ever collide with.
        Key = $"pending:{Guid.NewGuid():N}",
        DisplaySubject = $"Will be generated - certificate for {subjectName}",
        DisplayThumbprint = "(generated during install)",
        PendingSubjectName = subjectName,
    };
}
