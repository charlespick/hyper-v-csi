namespace HyperVCsiAgent.Installer.Actions.Tests;

public sealed class CommandLineArgsTests
{
    [Fact]
    public void Require_ReturnsTheValueFollowingItsFlag()
    {
        var args = CommandLineArgs.Parse(["--output", "C:\\out.json", "--csv-volumes-root", "C:\\vols"]);

        Assert.Equal("C:\\out.json", args.Require("output"));
        Assert.Equal("C:\\vols", args.Require("csv-volumes-root"));
    }

    [Fact]
    public void Require_MissingFlag_Throws()
    {
        var args = CommandLineArgs.Parse(["--output", "C:\\out.json"]);

        Assert.Throws<ArgumentException>(() => args.Require("csv-volumes-root"));
    }

    [Fact]
    public void Optional_MissingFlag_ReturnsNull()
    {
        var args = CommandLineArgs.Parse([]);

        Assert.Null(args.Optional("tls-host-name"));
    }

    [Fact]
    public void OptionalList_SplitsOnSemicolonAndTrims()
    {
        var args = CommandLineArgs.Parse(["--values", " AA ; BB;CC "]);

        Assert.Equal(["AA", "BB", "CC"], args.OptionalList("values"));
    }

    [Fact]
    public void OptionalList_MissingFlag_ReturnsEmpty()
    {
        var args = CommandLineArgs.Parse([]);

        Assert.Empty(args.OptionalList("values"));
    }

    [Fact]
    public void Parse_ArgumentNotStartingWithFlag_Throws()
    {
        Assert.Throws<ArgumentException>(() => CommandLineArgs.Parse(["stray-value"]));
    }

    [Fact]
    public void Parse_FlagWithoutValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => CommandLineArgs.Parse(["--output"]));
    }
}
