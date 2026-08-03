using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperVCsiAgent.Core;

/// <summary>
/// Single source of truth for the agent's JSON wire format, applied to the
/// service's HTTP JSON options and pinned by JobWireFormatTests. Enums
/// serialize as their PascalCase names ("Succeeded", not 2) to match the
/// JobStatus string constants in csi-driver/internal/agentclient/client.go;
/// property names are camelCase via the host's web defaults, matching that
/// file's json tags.
/// </summary>
public static class AgentJson
{
    public static void Apply(JsonSerializerOptions options) =>
        options.Converters.Add(new JsonStringEnumConverter());
}
