// -----------------------------------------------------------------------
// <copyright file="LoopbackHostOriginMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class LoopbackHostOriginMutationTests
{
    [Theory]
    [InlineData("localhost:5199")]
    [InlineData("LOCALHOST:5199")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:5199")]
    [InlineData("[::1]:5199")]
    public void Loopback_host_name_without_an_origin_is_admitted(string host)
    {
        Assert.Null(LoopbackHostOriginPolicy.FindRefusal(new HostString(host), StringValues.Empty));
    }

    [Theory]
    [InlineData("rebind.example:5199")]
    [InlineData("localhost.evil.example")]
    [InlineData("127.0.0.1.evil.example:5199")]
    [InlineData("localhost:abc")]
    [InlineData("")]
    public void Foreign_host_name_is_refused_even_with_a_matching_origin(string host)
    {
        var origin = new StringValues("http://" + host);

        var reason = LoopbackHostOriginPolicy.FindRefusal(new HostString(host), origin);

        Assert.Contains("Host", reason);
    }

    [Theory]
    [InlineData("localhost:5199", "http://localhost:5199")]
    [InlineData("LOCALHOST:5199", "http://localhost:5199")]
    public void Origin_equal_to_http_scheme_plus_host_is_admitted(string host, string origin)
    {
        Assert.Null(LoopbackHostOriginPolicy.FindRefusal(new HostString(host), new StringValues(origin)));
    }

    [Theory]
    [InlineData("127.0.0.1:5199", "http://evil.example")]
    [InlineData("localhost:5199", "null")]
    [InlineData("localhost:5199", "http://localhost:3000")]
    [InlineData("localhost:5199", "https://localhost:5199")]
    [InlineData("localhost:5199", "")]
    public void Origin_that_differs_from_http_scheme_plus_host_is_refused(string host, string origin)
    {
        var reason = LoopbackHostOriginPolicy.FindRefusal(new HostString(host), new StringValues(origin));

        Assert.Contains("Origin", reason);
    }

    [Fact]
    public void Two_origin_headers_are_refused()
    {
        var origins = new StringValues(["http://localhost:5199", "http://localhost:5199"]);

        var reason = LoopbackHostOriginPolicy.FindRefusal(new HostString("localhost:5199"), origins);

        Assert.Contains("Origin", reason);
    }
}
