using System.Text.Json;
using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Pins the HTTP wire format to what the Go client
/// (csi-driver/internal/agentclient/client.go) decodes: camelCase property
/// names and PascalCase status strings. If one of these assertions has to
/// change, the Go structs and AgentJson must change together.
/// </summary>
public class JobWireFormatTests
{
    private static JsonSerializerOptions WireOptions()
    {
        // Same base the minimal-API host uses (web defaults) plus the agent's
        // own configuration.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        AgentJson.Apply(options);
        return options;
    }

    [Theory]
    [InlineData(JobStatus.Pending, "Pending")]
    [InlineData(JobStatus.Running, "Running")]
    [InlineData(JobStatus.Succeeded, "Succeeded")]
    [InlineData(JobStatus.Failed, "Failed")]
    public void JobStatus_SerializesAsThePascalCaseName(JobStatus status, string expected)
    {
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(status, WireOptions()));
    }

    [Fact]
    public void Job_SerializesWithTheFieldNamesTheGoClientExpects()
    {
        var job = new Job
        {
            Id = "abc123",
            IdempotencyKey = "pvc-1",
            OperationType = "CreateVolume",
            Target = "vol-pvc-1",
            Status = JobStatus.Failed,
            Error = "boom",
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(job, WireOptions()));
        var root = document.RootElement;

        Assert.Equal("abc123", root.GetProperty("id").GetString());
        Assert.Equal("pvc-1", root.GetProperty("idempotencyKey").GetString());
        Assert.Equal("CreateVolume", root.GetProperty("operationType").GetString());
        Assert.Equal("vol-pvc-1", root.GetProperty("target").GetString());
        Assert.Equal("Failed", root.GetProperty("status").GetString());
        Assert.Equal("boom", root.GetProperty("error").GetString());
    }
}
