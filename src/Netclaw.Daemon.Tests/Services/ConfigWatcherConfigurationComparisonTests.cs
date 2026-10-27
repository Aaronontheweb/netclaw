// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherConfigurationComparisonTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherConfigurationComparisonTests
{
    [Theory]
    [InlineData("Daemon:Host", "Alpha", "Daemon:Host", "Alpha", false)]
    [InlineData("Daemon:Host", "Alpha", "daemon:host", "Alpha", false)]
    [InlineData("Daemon:Host", "Alpha", "Daemon:Host", "alpha", true)]
    [InlineData(null, null, "Daemon:Host", "Alpha", true)]
    [InlineData("Daemon:Host", "Alpha", null, null, true)]
    [InlineData("Daemon:Host", null, "Daemon:Port", null, true)]
    [InlineData("Daemon:Host", null, null, null, true)]
    [InlineData("Daemon:Host", null, "Daemon:Host", "", true)]
    [InlineData("Providers:p:Type", "ollama", "Providers:p:Type", "openai", true)]
    [InlineData("Providers:p:Endpoint", "http://localhost:18081", "Providers:p:Endpoint", "http://localhost:18082", true)]
    [InlineData("Providers:p:AuthMethod", "ApiKey", "Providers:p:AuthMethod", "OAuthDevice", true)]
    [InlineData("Providers:p:ApiKey", "synthetic-key-before", "Providers:p:ApiKey", "synthetic-key-after", true)]
    [InlineData("Providers:p:VendorOptions:OAuthAccessToken", "Alpha", "Providers:p:VendorOptions:OAuthAccessToken", "alpha", true)]
    public void Comparison_preserves_key_presence_and_exact_values(
        string? generationKey,
        string? generationValue,
        string? candidateKey,
        string? candidateValue,
        bool expectedChange)
    {
        var generation = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (generationKey is not null)
            generation.Add(generationKey, generationValue);

        KeyValuePair<string, string?>[] candidate = candidateKey is null
            ? []
            : [new(candidateKey, candidateValue)];

        Assert.Equal(expectedChange, ConfigWatcherService.HasConfigurationChanged(generation, candidate));
    }
}
