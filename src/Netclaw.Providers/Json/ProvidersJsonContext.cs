// -----------------------------------------------------------------------
// <copyright file="ProvidersJsonContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;
using System.Text.Json;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OAuth;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;
using Netclaw.Providers.VeniceAi;

namespace Netclaw.Providers.Json;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(DeviceAuthorizationResponse))]
[JsonSerializable(typeof(OpenAiDeviceFlowService.OpenAiAuthCodeResponse))]
[JsonSerializable(typeof(CopilotTokenExchanger.TokenResponse))]
[JsonSerializable(typeof(OpenRouterVendorOptions))]
[JsonSerializable(typeof(OllamaVendorOptions))]
[JsonSerializable(typeof(VeniceAiVendorOptions))]
[JsonSerializable(typeof(GitHubCopilotAuthOptions))]
internal sealed partial class ProvidersJsonContext : JsonSerializerContext
{
    internal static readonly ProvidersJsonContext VendorOptions = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });
}
