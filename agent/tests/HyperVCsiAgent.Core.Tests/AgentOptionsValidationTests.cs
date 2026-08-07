using HyperVCsiAgent.Core.Configuration;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Everything here is checked at startup so a bad config fails the clustered
/// role, which the cluster reports, rather than failing every connection while
/// the role still shows Online.
/// </summary>
public class AgentOptionsValidationTests
{
    [Theory]
    // What openssl actually prints, pasted whole. The label's own hex letters
    // ('a', '1', 'F', 'e') get folded into the value, producing a 44-character
    // pin that matches nothing - the operator sees a fingerprint that looks
    // correct in the config and a driver locked out with a TLS error.
    [InlineData("sha1 Fingerprint=68:31:28:5A:B1:62:AC:3C:47:2B:39:EC:19:6A:0F:06:D6:7B:2A:52")]
    [InlineData("SHA1 Fingerprint=68312 85AB162AC3C472B39EC196A0F06D67B2A52")]
    [InlineData("68:31:28:5A")]
    [InlineData("not a thumbprint at all")]
    [InlineData("")]
    public void Validate_ThumbprintThatIsNotFortyHexCharacters_IsRejected(string thumbprint)
    {
        var options = NewOptions();
        options.Authentication.AllowedClientCertificateThumbprints = [thumbprint];

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("hex characters", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("6831285AB162AC3C472B39EC196A0F06D67B2A52")]
    [InlineData("68:31:28:5A:B1:62:AC:3C:47:2B:39:EC:19:6A:0F:06:D6:7B:2A:52")]
    [InlineData("68 31 28 5a b1 62 ac 3c 47 2b 39 ec 19 6a 0f 06 d6 7b 2a 52")]
    public void Validate_ThumbprintInAnyFormatAnOperatorMightPaste_IsAccepted(string thumbprint)
    {
        var options = NewOptions();
        options.Authentication.AllowedClientCertificateThumbprints = [thumbprint];

        options.Validate();
    }

    [Theory]
    [InlineData("LocalMachne", "My")]
    [InlineData("LocalMachine", "Personal")]
    public void Validate_MistypedCertificateStore_IsRejected(string storeLocation, string storeName)
    {
        // Otherwise Enum.Parse throws inside every TLS handshake instead, long
        // after the role has come up looking healthy.
        var options = NewOptions();
        options.Tls.HostName = "agent.test";
        options.Tls.AllowedThumbprints = ["6831285AB162AC3C472B39EC196A0F06D67B2A52"];
        options.Tls.StoreLocation = storeLocation;
        options.Tls.StoreName = storeName;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_TlsNotConfigured_DoesNotCheckTheStore()
    {
        // Development runs without TLS; Program.cs is what refuses that
        // anywhere else.
        var options = NewOptions();
        options.Tls.StoreLocation = "nonsense";

        options.Validate();
    }

    [Fact]
    public void Validate_HostNameSetWithNoAllowedThumbprints_IsRejected()
    {
        // Unlike the old subject-match, nothing else identifies which store
        // certificate the agent should serve.
        var options = NewOptions();
        options.Tls.HostName = "agent.test";

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("AllowedThumbprints", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MalformedServerThumbprint_IsRejected()
    {
        var options = NewOptions();
        options.Tls.HostName = "agent.test";
        options.Tls.AllowedThumbprints = ["not a thumbprint"];

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("hex characters", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_HostNameAndWellFormedThumbprints_IsAccepted()
    {
        var options = NewOptions();
        options.Tls.HostName = "agent.test";
        options.Tls.AllowedThumbprints = ["6831285AB162AC3C472B39EC196A0F06D67B2A52"];

        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingSnapshotsRoot_IsRejected(string root)
    {
        // Required exactly as CsvVolumesRoot is. There is no defensible default:
        // a relative path would resolve against whatever directory the SCM
        // started the process in, which for a clustered role is C:\Windows\System32.
        var options = NewOptions();
        options.CsvSnapshotsRoot = root;

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(AgentOptions.CsvSnapshotsRoot), failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveSnapshotCopyConcurrency_IsRejected(int copies)
    {
        var options = NewOptions();
        options.MaxConcurrentSnapshotCopies = copies;

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(AgentOptions.MaxConcurrentSnapshotCopies), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NonPositiveSnapshotCopyTimeout_IsRejected()
    {
        var options = NewOptions();
        options.SnapshotCopyTimeout = TimeSpan.Zero;

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(AgentOptions.SnapshotCopyTimeout), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SnapshotCopyTimeout_IsNotTheDiskOperationTimeout()
    {
        // A copy is bulk I/O over a CSV, not a management call. Defaulting the
        // two to the same clock would make every large snapshot restart forever,
        // each attempt discarding the last, and it would do so silently.
        var options = NewOptions();

        Assert.True(options.SnapshotCopyTimeout > options.DiskOperationTimeout);
        Assert.True(options.SnapshotCopyTimeout >= TimeSpan.FromHours(1));
    }

    private static AgentOptions NewOptions() =>
        new() { CsvVolumesRoot = "/tmp/volumes", CsvSnapshotsRoot = "/tmp/snapshots" };
}
