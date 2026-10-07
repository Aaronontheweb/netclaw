// -----------------------------------------------------------------------
// <copyright file="OtlpSignalEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Configuration;
using OpenTelemetry.Exporter;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class OtlpSignalEndpointTests
{
    [Theory]
    [InlineData("http://collector:4318", "v1/logs", "http://collector:4318/v1/logs")]
    [InlineData("http://collector:4318/", "v1/metrics", "http://collector:4318/v1/metrics")]
    [InlineData("https://gw.example/otel", "v1/metrics", "https://gw.example/otel/v1/metrics")]
    [InlineData("http://collector:4318/v1/logs", "v1/logs", "http://collector:4318/v1/logs")]
    [InlineData("https://gw.example/otel/v1/metrics/", "v1/metrics", "https://gw.example/otel/v1/metrics/")]
    public void HttpProtobuf_AppendsTheSignalPathToABaseUrl_AndKeepsAFullSignalUrl(
        string configured, string signalPath, string expected)
    {
        var resolved = TelemetryRegistrationExtensions.ResolveSignalEndpoint(
            new Uri(configured), OtlpExportProtocol.HttpProtobuf, signalPath);

        Assert.Equal(expected, resolved.AbsoluteUri);
    }

    [Theory]
    [InlineData("v1/logs")]
    [InlineData("v1/metrics")]
    public void Grpc_UsesTheEndpointUnchanged(string signalPath)
    {
        var configured = new Uri("http://collector:4317");

        var resolved = TelemetryRegistrationExtensions.ResolveSignalEndpoint(
            configured, OtlpExportProtocol.Grpc, signalPath);

        Assert.Equal(configured, resolved);
    }
}
