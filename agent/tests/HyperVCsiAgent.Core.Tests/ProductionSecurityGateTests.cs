using HyperVCsiAgent.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// The agent must never come up serving plaintext or without client
/// authentication outside Development. This is the one guard responsible for
/// that guarantee, so it is exercised directly rather than only through a
/// Program.cs that every test host boots under the Development environment.
/// </summary>
public class ProductionSecurityGateTests
{
    [Fact]
    public void Enforce_NotDevelopment_TlsNotConfigured_Throws()
    {
        var options = NewOptions();

        Assert.Throws<InvalidOperationException>(
            () => ProductionSecurityGate.Enforce(isDevelopment: false, options, NullLogger.Instance));
    }

    [Fact]
    public void Enforce_NotDevelopment_TlsConfiguredButAuthenticationNotConfigured_Throws()
    {
        var options = NewOptions();
        options.Tls.HostName = "agent.test";

        Assert.Throws<InvalidOperationException>(
            () => ProductionSecurityGate.Enforce(isDevelopment: false, options, NullLogger.Instance));
    }

    [Fact]
    public void Enforce_NotDevelopment_TlsAndAuthenticationConfigured_DoesNotThrow()
    {
        var options = NewOptions();
        options.Tls.HostName = "agent.test";
        options.Authentication.AllowedClientCertificateThumbprints = ["6831285AB162AC3C472B39EC196A0F06D67B2A52"];

        ProductionSecurityGate.Enforce(isDevelopment: false, options, NullLogger.Instance);
    }

    [Fact]
    public void Enforce_Development_NeitherConfigured_DoesNotThrow()
    {
        var options = NewOptions();

        ProductionSecurityGate.Enforce(isDevelopment: true, options, NullLogger.Instance);
    }

    [Fact]
    public void Enforce_Development_BothConfigured_DoesNotThrow()
    {
        var options = NewOptions();
        options.Tls.HostName = "agent.test";
        options.Authentication.AllowedClientCertificateThumbprints = ["6831285AB162AC3C472B39EC196A0F06D67B2A52"];

        ProductionSecurityGate.Enforce(isDevelopment: true, options, NullLogger.Instance);
    }

    private static AgentOptions NewOptions() => new() { CsvVolumesRoot = "/tmp/volumes" };
}
