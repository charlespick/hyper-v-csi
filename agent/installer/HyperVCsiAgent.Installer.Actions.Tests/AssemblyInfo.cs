using System.Runtime.Versioning;

// Mirrors HyperVCsiAgent.Installer.Actions itself: every command under test
// here is only ever meant to run as part of a Windows MSI, so the whole
// assembly is Windows-only rather than guarding each call site individually.
[assembly: SupportedOSPlatform("windows")]
