using Microsoft.Extensions.Logging;

namespace HyperVCsiAgent.Core.Configuration;

/// <summary>
/// How much of the agent's own logging actually leaves the process.
/// Information by default because that is what the other 100+ call sites
/// across this codebase were written assuming an operator can see - a create,
/// attach or checkpoint that succeeds, and everything that fails outright.
/// Debug adds the entry/parameter logging behind that (every WQL query
/// issued, every CIM call made) for diagnosing a specific incident, not for
/// routine operation - a clustered role logging at Debug all the time would
/// fill the Application log with noise no operator asked for.
/// </summary>
/// <remarks>
/// Deliberately never written by the installer - see <c>agent.config.example.json</c>'s
/// own <c>//Logging</c> entry. Turning this up is a diagnostic act an operator
/// takes deliberately, on one node, for as long as an incident needs it, the
/// same reasoning that keeps <see cref="AgentOptions.Tls"/> and
/// <see cref="AgentOptions.Authentication"/> out of the installer's own
/// generated fields where the wizard has no page for them. Not validated in
/// <see cref="AgentOptions.Validate"/>: an unrecognized level string already
/// fails configuration binding with .NET's own clear error before this class
/// is ever constructed, so a redundant check here would only restate that.
/// </remarks>
public sealed class LoggingOptions
{
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
