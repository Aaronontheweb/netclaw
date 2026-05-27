// -----------------------------------------------------------------------
// <copyright file="VeniceAiVendorOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers.VeniceAi;

/// <summary>
/// Operator-facing knobs for the Venice.ai provider, bound from
/// <c>Providers:&lt;name&gt;:VendorOptions</c>.
/// </summary>
public sealed class VeniceAiVendorOptions : IVendorOptions
{
    /// <summary>
    /// Allow Venice to prepend its own system prompt to every chat completion.
    /// Defaults to <c>false</c> — Netclaw's <c>SystemPromptAssembler</c> output
    /// must be the first system message the model sees so identity grounding
    /// (SOUL.md / AGENTS.md / TOOLING.md) and compaction context-budget math
    /// remain authoritative. Operators who explicitly want Venice's prefix
    /// can set this to <c>true</c>.
    /// </summary>
    public bool IncludeVeniceSystemPrompt { get; set; }
}
