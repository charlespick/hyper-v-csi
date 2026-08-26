using System.ComponentModel;
using System.Diagnostics;
using HyperVCsiAgent.Installer.Actions;

// Thin entry point: this file carries no version logic of its own. Every rule
// that decides what a version string looks like lives in VersionPipeline.cs
// (compiled into this project as shared source - see this project's .csproj
// comment) so the build and HyperVCsiAgent.Installer.Actions.Tests's unit
// tests can never disagree about the rules; this program only collects the
// two real-world inputs the pipeline needs (a git describe reading and a
// clock reading) and prints what it returns.

var repoPath = ParseRepoPath(args) ?? Directory.GetCurrentDirectory();

string describeOutput;
bool workingTreeDirty;

try
{
    describeOutput = RunGitDescribe(repoPath);
    workingTreeDirty = RunGitStatusPorcelain(repoPath);
}
catch (GitUnavailableException ex)
{
    // A build that cannot get a trustworthy answer out of git - a
    // missing executable, a --repo path that is not a working tree, or a
    // shallow clone whose tags were never fetched - must fail loudly rather
    // than invent a fallback version. There is deliberately no default
    // version anywhere in this program.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var buildKey = VersionPipeline.ComputeBuildKey(DateTimeOffset.UtcNow);
var derived = VersionPipeline.Derive(describeOutput, buildKey, workingTreeDirty);

// A later chunk's MSBuild target parses this exact shape off stdout: one
// "Name=Value" line per value, in this order, with these exact names, and
// nothing else on stdout. Keep it this way - any diagnostic output this
// program wants to add belongs on stderr instead.
Console.WriteLine($"HyperVCsiBuildKey={buildKey}");
Console.WriteLine($"HyperVCsiVersion={derived.Semver}");
Console.WriteLine($"HyperVCsiInformationalVersion={derived.InformationalVersion}");
Console.WriteLine($"HyperVCsiMsiVersion={derived.MsiVersion}");
Console.WriteLine($"HyperVCsiFileVersion={derived.FileVersion}");
Console.WriteLine($"HyperVCsiAssemblyVersion={derived.AssemblyVersion}");

return 0;

static string? ParseRepoPath(string[] arguments)
{
    for (var i = 0; i < arguments.Length; i++)
    {
        if (string.Equals(arguments[i], "--repo", StringComparison.Ordinal) && i + 1 < arguments.Length)
        {
            return arguments[i + 1];
        }
    }

    return null;
}

static string RunGitDescribe(string repoPath)
{
    // `git describe` cannot distinguish its own failure modes: "this
    // repository genuinely has no version tag yet" (an ordinary state, and
    // the reason VersionPipeline has a 0.0.0-0.<K> case at all), "this path
    // is not a working tree", and "this is a shallow clone that was never
    // given the tags it does have" all come back as the same non-zero exit
    // and the same "No names found" on stderr. Treating all three as the
    // benign one is how a build silently produces a fabricated version, so
    // a shallow clone's failure needs to be loud rather than falling back
    // to a default. The two loud cases are each checked directly, up front,
    // and only an answer that survives both is allowed to be empty.
    var insideWorkTree = RunGit(repoPath, "rev-parse", "--is-inside-work-tree");
    if (!insideWorkTree.Started)
    {
        throw new GitUnavailableException($"Could not run git in '{repoPath}': {insideWorkTree.StdErr}");
    }

    if (insideWorkTree.ExitCode != 0 || !string.Equals(insideWorkTree.StdOut.Trim(), "true", StringComparison.Ordinal))
    {
        throw new GitUnavailableException(
            $"'{repoPath}' is not inside a git working tree, so no version can be derived from it. " +
            "Pass --repo pointing at the checkout, or run this from inside it.");
    }

    if (IsShallowRepository(repoPath))
    {
        throw new GitUnavailableException(
            $"'{repoPath}' is a shallow clone, so `git describe` cannot be trusted to see the version tags. " +
            "Fetch the full history and tags first (in CI: actions/checkout with fetch-depth: 0).");
    }

    var result = RunGit(repoPath, "describe", "--tags", "--long");
    // --dirty is deliberately never passed here: it would insert its own
    // suffix (e.g. "-dirty") after the fixed "-<N>-g<sha>" tail that
    // VersionPipeline.ParseGitDescribe splits off from the right, corrupting
    // the one thing that makes that split unambiguous. The dirty flag is
    // sourced independently below, from `git status --porcelain`.
    if (!result.Started)
    {
        throw new GitUnavailableException($"Could not run 'git describe' in '{repoPath}': {result.StdErr}");
    }

    // Only now is an empty answer trustworthy. The path has been proved to be
    // a full, non-shallow working tree, so "no names found" really does mean
    // this repository carries no version tag yet, and VersionPipeline.Derive's
    // 0.0.0-0.<K> fallback is the right answer rather than a cover-up.
    if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
    {
        return string.Empty;
    }

    return result.StdOut;
}

static bool IsShallowRepository(string repoPath)
{
    var result = RunGit(repoPath, "rev-parse", "--is-shallow-repository");

    // Only a definite "true" is treated as shallow. `--is-shallow-repository`
    // predates nothing this project supports, but if it ever fails to answer,
    // failing the build over an inconclusive shallowness check would be worse
    // than proceeding - the describe reading below still has to succeed on its
    // own merits, and a shallow clone with no tags is caught there.
    return result.Started
        && result.ExitCode == 0
        && string.Equals(result.StdOut.Trim(), "true", StringComparison.Ordinal);
}

static bool RunGitStatusPorcelain(string repoPath)
{
    var result = RunGit(repoPath, "status", "--porcelain");
    if (!result.Started)
    {
        throw new GitUnavailableException($"Could not run 'git status' in '{repoPath}': {result.StdErr}");
    }

    // Unlike the describe command, a non-zero exit here is not treated as
    // fatal or even reported - the dirty flag only ever adds a cosmetic
    // ".dirty" suffix to build metadata, which SemVer ignores for precedence,
    // so it never changes which version wins a Burn comparison, and there is no
    // case where failing to determine it should fail the whole build.
    return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
}

static GitInvocationResult RunGit(string repoPath, params string[] arguments)
{
    var startInfo = new ProcessStartInfo("git")
    {
        WorkingDirectory = repoPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    try
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new GitInvocationResult(Started: false, ExitCode: -1, StdOut: string.Empty, StdErr: "Process.Start returned null.");
        }

        // Read both streams before WaitForExit so a chatty stderr (or a large
        // describe/status output) can never deadlock against a full pipe
        // buffer neither side is draining yet.
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new GitInvocationResult(Started: true, ExitCode: process.ExitCode, StdOut: stdout, StdErr: stderr);
    }
    catch (Win32Exception ex)
    {
        // The executable itself could not be launched - typically because
        // git is not on PATH. Reported as "did not start" rather than as an
        // exit code so callers can tell it apart from git running and
        // answering; RunGitDescribe treats both as fatal, RunGitStatusPorcelain
        // only the former.
        return new GitInvocationResult(Started: false, ExitCode: -1, StdOut: string.Empty, StdErr: ex.Message);
    }
}

internal readonly record struct GitInvocationResult(bool Started, int ExitCode, string StdOut, string StdErr);

internal sealed class GitUnavailableException(string message) : Exception(message);
