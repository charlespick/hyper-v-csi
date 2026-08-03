using System.Text.Json;
using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Turns a POST /v1/jobs body into the delegate the job store will run. The
/// payload is decoded here, before the job is created, so a malformed request
/// comes back as a 400 the controller can see immediately rather than as a job
/// that fails a moment later.
/// </summary>
public sealed class JobDispatcher(IVhdxService vhdxService)
{
    public const string CreateVolume = "CreateVolume";

    /// <exception cref="InvalidJobRequestException">
    /// The operation is unknown or its payload is unusable.
    /// </exception>
    public Func<Job, CancellationToken, Task> Resolve(string operationType, JsonElement payload, JsonSerializerOptions jsonOptions)
    {
        switch (operationType)
        {
            case CreateVolume:
                var request = Decode<CreateVolumePayload>(payload, jsonOptions);
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    throw new InvalidJobRequestException("payload.name is required");
                }

                if (request.SizeBytes <= 0)
                {
                    throw new InvalidJobRequestException("payload.sizeBytes must be positive");
                }

                return async (job, cancellationToken) =>
                    job.Result = await vhdxService.CreateAsync(request.Name, request.SizeBytes, cancellationToken)
                        .ConfigureAwait(false);

            default:
                throw new InvalidJobRequestException($"unsupported operationType {operationType}");
        }
    }

    private static T Decode<T>(JsonElement payload, JsonSerializerOptions jsonOptions)
    {
        if (payload.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidJobRequestException("payload must be an object");
        }

        try
        {
            return payload.Deserialize<T>(jsonOptions)
                ?? throw new InvalidJobRequestException("payload must not be null");
        }
        catch (JsonException ex)
        {
            throw new InvalidJobRequestException($"payload is not valid for this operation: {ex.Message}");
        }
    }
}
