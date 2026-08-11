namespace HyperVCsiAgent.Installer.Actions.Tests;

public sealed class ValidateThumbprintsCommandTests
{
    [Fact]
    public void WellFormedThumbprints_Succeed()
    {
        var result = ValidateThumbprintsCommand.Run([
            "--values", "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4;B1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4",
        ]);

        Assert.Equal(0, result);
    }

    [Fact]
    public void AcceptsColonsAndLowercase_TheSameNormalizationTheAgentUses()
    {
        var result = ValidateThumbprintsCommand.Run([
            "--values", "a1:b2:c3:d4:e5:f6:07:18:29:3a:4b:5c:6d:7e:8f:90:a1:b2:c3:d4",
        ]);

        Assert.Equal(0, result);
    }

    [Fact]
    public void MalformedThumbprint_Fails()
    {
        var result = ValidateThumbprintsCommand.Run(["--values", "not-a-thumbprint"]);

        Assert.Equal(1, result);
    }

    [Fact]
    public void NoValues_Fails()
    {
        var result = ValidateThumbprintsCommand.Run([]);

        Assert.Equal(1, result);
    }
}
