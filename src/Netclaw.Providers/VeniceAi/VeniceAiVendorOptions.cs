// -----------------------------------------------------------------------
// <copyright file="VeniceAiVendorOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers.VeniceAi;

/// <summary>
/// Operator-tunable knobs for the Venice.ai provider. Bound from
/// <c>Providers:&lt;name&gt;:VendorOptions</c>.
/// </summary>
public sealed class VeniceAiVendorOptions : IVendorOptions
{
    /// <summary>
    /// When <c>false</c> (default), Netclaw forces
    /// <c>venice_parameters.include_venice_system_prompt = false</c> on every
    /// outbound request so Venice's default "uncensored" system prompt never
    /// prepends to Netclaw's assembled identity context. Operators who
    /// explicitly want Venice's system prompt set this to <c>true</c>; the
    /// override pipeline policy is then not attached.
    /// </summary>
    public bool IncludeVeniceSystemPrompt { get; set; } = false;
}
