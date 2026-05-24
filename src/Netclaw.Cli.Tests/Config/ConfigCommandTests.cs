// -----------------------------------------------------------------------
// <copyright file="ConfigCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

public sealed class ConfigCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ConfigCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void PreflightOrRefuse_NoInstall_RefusesWithHint()
    {
        var output = new StringWriter();

        var ok = ConfigCommand.PreflightOrRefuse(_paths, output);

        Assert.False(ok, "Missing install SHALL not preflight-pass.");
        var text = output.ToString();
        Assert.Contains("netclaw init", text, StringComparison.Ordinal);
        Assert.Contains("no installation found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreflightOrRefuse_ExistingInstall_Passes()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{"configVersion":1}""");

        var output = new StringWriter();
        var ok = ConfigCommand.PreflightOrRefuse(_paths, output);

        Assert.True(ok);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void WriteHelp_DescribesRoutedHandoffs()
    {
        var output = new StringWriter();
        ConfigCommand.WriteHelp(output);
        var text = output.ToString();

        Assert.Contains("netclaw provider", text, StringComparison.Ordinal);
        Assert.Contains("netclaw model", text, StringComparison.Ordinal);
        Assert.Contains("netclaw mcp permissions", text, StringComparison.Ordinal);
        Assert.Contains("Refusal:", text, StringComparison.Ordinal);
    }
}
