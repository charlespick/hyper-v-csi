using System;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>One row of the Certificate page's certificate table.</summary>
internal sealed record CertificateEntry(string SubjectName, string Thumbprint, DateTimeOffset NotAfter);
