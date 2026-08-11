namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Minimal <c>--flag value</c> parsing shared by every subcommand. Not a
/// general-purpose CLI framework - there are four commands, each with a
/// handful of required or optional flags, which does not carry the weight of
/// an external dependency.
/// </summary>
internal sealed class CommandLineArgs
{
    private readonly Dictionary<string, string> _values;

    private CommandLineArgs(Dictionary<string, string> values) => _values = values;

    public static CommandLineArgs Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"expected a --flag, got '{args[i]}'");
            }

            var flag = args[i][2..];
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"--{flag} is missing its value");
            }

            values[flag] = args[++i];
        }

        return new CommandLineArgs(values);
    }

    public string Require(string flag) => _values.TryGetValue(flag, out var value)
        ? value
        : throw new ArgumentException($"--{flag} is required");

    public string? Optional(string flag) => _values.GetValueOrDefault(flag);

    /// <summary>
    /// A caller-chosen separator list, since thumbprints and other multi-value
    /// flags are passed as one string (the wizard collects them as
    /// newline-separated text; MSI properties collapse newlines, so the
    /// installer joins them with ';' before invoking this exe).
    /// </summary>
    public string[] OptionalList(string flag) => Optional(flag) is { } value
        ? value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];
}
