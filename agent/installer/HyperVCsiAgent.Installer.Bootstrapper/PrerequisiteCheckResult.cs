namespace HyperVCsiAgent.Installer.Bootstrapper;

internal enum PrerequisiteStatus
{
    Pass,
    Warn,
}

/// <summary>
/// One row of the Prerequisites page's checklist. Warn-only by design - see
/// PrerequisiteChecks for why neither check today blocks Next.
/// </summary>
internal sealed record PrerequisiteCheckResult(string Name, PrerequisiteStatus Status, string Detail);
