// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginApi.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Configuration;

/// <summary>Wire contracts for managed Git skill plugin operations.</summary>
public static class GitSkillPluginApi
{
    public enum InstallReferenceKind
    {
        DefaultBranch,
        Branch,
        Tag,
        Commit,
    }

    public enum PluginStatus
    {
        Disabled,
        Installed,
        NotInstalled,
        Stale,
    }

    public sealed class InstallRequest : IWireType
    {
        public required string Repository { get; init; }
        public string? Name { get; init; }
        public string Format { get; init; } = "codex";
        public string? Subdirectory { get; init; }
        public InstallReferenceKind ReferenceKind { get; init; }
        public string? Reference { get; init; }
        public int TimeoutSeconds { get; init; } = 60;
    }

    public sealed class InstallResponse : IWireType
    {
        public required int RestartGeneration { get; init; }
        public required PluginRow Plugin { get; init; }
    }

    public sealed class ListResponse : IWireType
    {
        public required List<PluginRow> Plugins { get; init; }
    }

    public sealed class PluginRow : IWireType
    {
        public required string Name { get; init; }
        public required string Repository { get; init; }
        public required string Format { get; init; }
        public string? Subdirectory { get; init; }
        public required GitSkillPluginReferenceKind ReferenceKind { get; init; }
        public required string Reference { get; init; }
        public required bool Enabled { get; init; }
        public required PluginStatus Status { get; init; }
        public string? InstalledCommit { get; init; }
        public string? LastObservedCommit { get; init; }
        public string? InstalledVersion { get; init; }
    }

    public sealed class SetEnabledRequest : IWireType
    {
        public required bool Enabled { get; init; }
    }

    public sealed class MutationResponse : IWireType
    {
        public required int RestartGeneration { get; init; }
        public required string Name { get; init; }
        public required bool Changed { get; init; }
    }
}
