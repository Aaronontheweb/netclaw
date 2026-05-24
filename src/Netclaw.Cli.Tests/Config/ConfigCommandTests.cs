// -----------------------------------------------------------------------
// <copyright file="ConfigCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

public sealed class ConfigCommandTests
{
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

    [Fact]
    public void WriteHelp_MentionsSpecCompliantRefusalMessage()
    {
        var output = new StringWriter();
        ConfigCommand.WriteHelp(output);
        var text = output.ToString();

        // The help text describes the SAME stderr string the production
        // path in Program.cs emits — keeps the two in lockstep without a
        // duplicate dead helper.
        Assert.Contains("No configuration found. Run `netclaw init` first.", text, StringComparison.Ordinal);
    }
}
