// -----------------------------------------------------------------------
// <copyright file="NetclawUserAgentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class NetclawUserAgentTests
{
    [Fact]
    public void Value_starts_with_Netclaw_product_token()
    {
        Assert.StartsWith("Netclaw/", NetclawUserAgent.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_includes_homepage_url()
    {
        Assert.Contains("https://netclaw.dev", NetclawUserAgent.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_includes_sha_marker()
    {
        Assert.Contains("sha=", NetclawUserAgent.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_parses_as_RFC7231_user_agent()
    {
        // ProductInfoHeaderValue is strict about whitespace, parens, and tokens.
        // If we generate junk that breaks parsing, every downstream HttpClient will
        // throw FormatException on header set — assert here instead of in prod.
        var ua = NetclawUserAgent.Value;
        var parsed = ProductInfoHeaderValue.TryParse(ua.Split(' ')[0], out _);
        Assert.True(parsed, $"Product token not parseable: {ua}");
    }

    [Fact]
    public void Component_header_name_is_X_Netclaw_Component()
    {
        Assert.Equal("X-Netclaw-Component", NetclawUserAgent.ComponentHeader);
    }
}
