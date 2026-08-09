using System.Text.Json;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;

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
            Targets = ["vol-pvc-1"],
            Status = JobStatus.Failed,
            Error = "boom",
            ErrorCode = AgentErrorCodes.AlreadyExists,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(job, WireOptions()));
        var root = document.RootElement;

        Assert.Equal("abc123", root.GetProperty("id").GetString());
        Assert.Equal("pvc-1", root.GetProperty("idempotencyKey").GetString());
        Assert.Equal("CreateVolume", root.GetProperty("operationType").GetString());
        Assert.Equal(["vol-pvc-1"], root.GetProperty("targets").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("Failed", root.GetProperty("status").GetString());
        Assert.Equal("boom", root.GetProperty("error").GetString());
        Assert.Equal("AlreadyExists", root.GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Job_SerializesItsResultByRuntimeType()
    {
        // The Go client decodes result into an operation-specific struct, so the
        // concrete payload's own field names have to make it onto the wire even
        // though Job.Result is declared as object.
        var job = new Job
        {
            Id = "abc123",
            IdempotencyKey = "pvc-1",
            OperationType = "CreateVolume",
            Targets = ["vol-pvc-1"],
            Status = JobStatus.Succeeded,
            Result = new CreateVolumeResult("pvc-1", 10737418240, AlreadyPresent: true),
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(job, WireOptions()));
        var result = document.RootElement.GetProperty("result");

        Assert.Equal("pvc-1", result.GetProperty("volumeId").GetString());
        Assert.Equal(10737418240, result.GetProperty("actualSizeBytes").GetInt64());
        Assert.True(result.GetProperty("alreadyPresent").GetBoolean());
    }

    [Fact]
    public void Job_OmitsResultAndErrorWhenUnset()
    {
        var job = new Job
        {
            Id = "abc123",
            IdempotencyKey = "pvc-1",
            OperationType = "CreateVolume",
            Targets = ["vol-pvc-1"],
            Status = JobStatus.Running,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(job, WireOptions()));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("result", out _));
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("errorCode", out _));
        Assert.False(root.TryGetProperty("queuedBehind", out _));
    }

    [Fact]
    public void Job_SerializesQueuedBehindWithTheFieldNamesTheGoClientExpects()
    {
        // Matches QueuedBehind in csi-driver/internal/agentclient/client.go.
        var job = new Job
        {
            Id = "abc123",
            IdempotencyKey = "pvc-2+node-a",
            OperationType = "AttachVolume",
            Targets = ["vm:node-a"],
            Status = JobStatus.Pending,
            QueuedBehind = new QueuedBehindInfo("vm:node-a", "CopySnapshot"),
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(job, WireOptions()));
        var queuedBehind = document.RootElement.GetProperty("queuedBehind");

        Assert.Equal("vm:node-a", queuedBehind.GetProperty("target").GetString());
        Assert.Equal("CopySnapshot", queuedBehind.GetProperty("operationType").GetString());
    }

    [Fact]
    public void SnapshotResult_SerializesWithTheFieldNamesTheGoControllerDecodes()
    {
        // Matches snapshotResult in csi-driver/internal/driver/controller.go.
        // Both zero-able fields are pinned as present-and-zero rather than
        // omitted: the Go side tests them for > 0 to decide whether to report
        // them at all, and a missing field would decode as 0 by accident where
        // this makes "not determinable yet" something the agent actually said.
        var result = new SnapshotResult("pvc-1~snapshot-abc", "pvc-1", 10737418240, 1770000000, ReadyToUse: false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, WireOptions()));
        var root = document.RootElement;

        Assert.Equal("pvc-1~snapshot-abc", root.GetProperty("snapshotId").GetString());
        Assert.Equal("pvc-1", root.GetProperty("sourceVolumeId").GetString());
        Assert.Equal(10737418240, root.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(1770000000, root.GetProperty("creationTimeUnixSeconds").GetInt64());
        Assert.False(root.GetProperty("readyToUse").GetBoolean());
    }

    [Fact]
    public void SnapshotResult_UnknownSizeAndTime_TravelAsZeroRatherThanBeingOmitted()
    {
        var result = new SnapshotResult("pvc-1~snapshot-abc", "pvc-1", 0, 0, ReadyToUse: false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, WireOptions()));
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(0, root.GetProperty("creationTimeUnixSeconds").GetInt64());
    }

    [Fact]
    public void ListSnapshotsResult_SerializesWithTheFieldNamesTheGoControllerDecodes()
    {
        var result = new ListSnapshotsResult(
            [new SnapshotResult("pvc-1~snapshot-abc", "pvc-1", 10737418240, 1770000000, ReadyToUse: true)],
            NextToken: "1");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, WireOptions()));
        var root = document.RootElement;

        var entry = Assert.Single(root.GetProperty("entries").EnumerateArray().ToList());
        Assert.Equal("pvc-1~snapshot-abc", entry.GetProperty("snapshotId").GetString());
        Assert.True(entry.GetProperty("readyToUse").GetBoolean());
        Assert.Equal("1", root.GetProperty("nextToken").GetString());
    }

    [Fact]
    public void ListSnapshotsResult_EmptyListing_IsAnEmptyArrayAndAnEmptyToken()
    {
        // Never null and never absent: the Go side treats a body it cannot
        // decode as a broken agent, and would otherwise report "no snapshots"
        // to a caller about to conclude they were all deleted.
        var result = new ListSnapshotsResult([], NextToken: string.Empty);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, WireOptions()));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("entries").ValueKind);
        Assert.Empty(root.GetProperty("entries").EnumerateArray().ToList());
        Assert.Equal(string.Empty, root.GetProperty("nextToken").GetString());
    }

    [Fact]
    public void CreateSnapshotPayload_DecodesTheFieldNamesTheGoControllerSends()
    {
        var payload = JsonSerializer.Deserialize<CreateSnapshotPayload>(
            """{"sourceVolumeId":"pvc-1","snapshotName":"snapshot-abc"}""", WireOptions());

        Assert.Equal("pvc-1", payload!.SourceVolumeId);
        Assert.Equal("snapshot-abc", payload.SnapshotName);
    }

    [Fact]
    public void ListSnapshotsPayload_DecodesTheFieldNamesTheGoControllerSends()
    {
        var payload = JsonSerializer.Deserialize<ListSnapshotsPayload>(
            """{"snapshotId":"pvc-1~a","sourceVolumeId":"pvc-1","startingToken":"2","maxEntries":10}""", WireOptions());

        Assert.Equal("pvc-1~a", payload!.SnapshotId);
        Assert.Equal("pvc-1", payload.SourceVolumeId);
        Assert.Equal("2", payload.StartingToken);
        Assert.Equal(10, payload.MaxEntries);
    }

    [Fact]
    public void SnapshotOperationTypes_AreTheExactStringsTheGoControllerSends()
    {
        // Paired with operationCreateSnapshot/operationDeleteSnapshot/
        // operationListSnapshots in csi-driver/internal/driver/controller.go.
        // A typo here compiles and shows up only as a 400 from the agent for
        // every snapshot the driver ever tries to take.
        Assert.Equal("CreateSnapshot", JobDispatcher.CreateSnapshot);
        Assert.Equal("DeleteSnapshot", JobDispatcher.DeleteSnapshot);
        Assert.Equal("ListSnapshots", JobDispatcher.ListSnapshots);
    }

    [Fact]
    public void AgentErrorCodes_AreTheExactStringsTheGoClientMatchesOn()
    {
        // These travel as strings and are compared literally against the
        // constants in csi-driver/internal/agentclient/client.go. A typo here
        // compiles, passes every test that goes through the constant, and shows
        // up only in production as a silent downgrade to codes.Internal -
        // turning a terminal answer into an infinite sidecar retry. Change
        // either side and you must change the other.
        Assert.Equal("InvalidArgument", AgentErrorCodes.InvalidArgument);
        Assert.Equal("AlreadyExists", AgentErrorCodes.AlreadyExists);
        Assert.Equal("ResourceExhausted", AgentErrorCodes.ResourceExhausted);
        Assert.Equal("FailedPrecondition", AgentErrorCodes.FailedPrecondition);
        Assert.Equal("Internal", AgentErrorCodes.Internal);
    }
}
