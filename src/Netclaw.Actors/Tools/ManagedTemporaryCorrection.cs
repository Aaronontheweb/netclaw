// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryCorrection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>Advice that asks an agent to submit a different tool call. A correction grants no authority.</summary>
internal abstract record ToolAgentCorrection
{
    private ToolAgentCorrection() { }

    /// <summary>Suggests the current run's managed temporary directory instead of a platform temporary root.</summary>
    internal sealed record ManagedTemporaryDirectorySuggested(
        string ManagedTemporaryDirectory,
        string PlatformTemporaryRoot) : ToolAgentCorrection;

    /// <summary>Suggests a native tool instead of invoking that tool name through the shell.</summary>
    internal sealed record NativeToolSuggested(ToolName ToolName) : ToolAgentCorrection;
}

/// <summary>Captures the execution-relevant arguments of one corrected tool call.</summary>
internal readonly record struct ManagedTemporaryCallSemantics(
    string ToolName,
    ApprovalShell? Shell,
    string? Command,
    bool HasExplicitWorkingDirectory,
    string? ExplicitWorkingDirectory,
    bool Background,
    TimeSpan Timeout,
    string? Path,
    string? Content,
    string? OldString,
    string? NewString,
    bool? ReplaceAll);

/// <summary>Binds one exact corrected call to the platform root and suggested managed directory.</summary>
internal readonly record struct ManagedTemporaryCorrectionKey(
    ManagedTemporaryCallSemantics Call,
    string PlatformTemporaryRoot,
    string ManagedTemporaryDirectory);

/// <summary>Describes an actor-owned change to the one-turn correction state.</summary>
internal abstract record ManagedTemporaryCorrectionChange
{
    private ManagedTemporaryCorrectionChange() { }

    internal sealed record Arm(ManagedTemporaryCorrectionKey Key) : ManagedTemporaryCorrectionChange;
    internal sealed record Consume(ManagedTemporaryCorrectionKey Key) : ManagedTemporaryCorrectionChange;
}

/// <summary>Provides a thread-safe, consume-once view of the keys that one actor committed.</summary>
internal sealed class ManagedTemporaryCorrectionDispatch
{
    internal static ManagedTemporaryCorrectionDispatch Empty { get; } = new([]);

    private readonly IReadOnlyList<ManagedTemporaryCorrectionKey> _armed;
    private readonly ConcurrentDictionary<ManagedTemporaryCorrectionKey, byte> _consumed = new();

    internal ManagedTemporaryCorrectionDispatch(IEnumerable<ManagedTemporaryCorrectionKey> armed)
        => _armed = Array.AsReadOnly(armed.ToArray());

    internal bool TryConsume(ManagedTemporaryCallSemantics call, out ManagedTemporaryCorrectionKey key)
    {
        foreach (var candidate in _armed)
        {
            if (candidate.Call != call || !_consumed.TryAdd(candidate, 0))
                continue;

            key = candidate;
            return true;
        }

        key = default;
        return false;
    }
}

/// <summary>Owns correction keys for one actor turn. A key is armed after commit and consumed once.</summary>
internal sealed class ManagedTemporaryCorrectionState
{
    private readonly HashSet<ManagedTemporaryCorrectionKey> _keys = [];

    internal ManagedTemporaryCorrectionDispatch Snapshot() => new(_keys);

    internal void Apply(ManagedTemporaryCorrectionChange? change)
    {
        switch (change)
        {
            case ManagedTemporaryCorrectionChange.Arm arm:
                _keys.Add(arm.Key);
                break;
            case ManagedTemporaryCorrectionChange.Consume consume:
                _keys.Remove(consume.Key);
                break;
        }
    }

    internal void Clear() => _keys.Clear();
}

/// <summary>Builds shared correction state for parent and child tool execution paths.</summary>
internal static class ManagedTemporaryCorrection
{
    /// <summary>Projects a tool call to the fields that must remain equal on an immediate retry.</summary>
    internal static ManagedTemporaryCallSemantics? BuildCallSemantics(
        FunctionCallContent toolCall,
        ToolCallMeta? meta,
        TimeSpan timeout,
        ApprovalShell shell = ApprovalShell.Bash)
    {
        if (string.Equals(toolCall.Name, ShellTool.ToolName, StringComparison.Ordinal))
        {
            var command = ToolArgumentHelper.GetString(toolCall.Arguments, "Command")
                ?? ToolArgumentHelper.GetString(toolCall.Arguments, "command");
            if (string.IsNullOrWhiteSpace(command))
                return null;

            var explicitCwd = ToolArgumentHelper.GetString(toolCall.Arguments, "WorkingDirectory");
            return new ManagedTemporaryCallSemantics(
                toolCall.Name,
                shell,
                command,
                !string.IsNullOrWhiteSpace(explicitCwd),
                explicitCwd,
                meta?.Background == true,
                timeout,
                null,
                null,
                null,
                null,
                null);
        }

        if (toolCall.Name is not (FileWriteTool.ToolName or FileEditTool.ToolName))
            return null;

        var path = ToolArgumentHelper.GetString(toolCall.Arguments, "Path")
            ?? ToolArgumentHelper.GetString(toolCall.Arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return new ManagedTemporaryCallSemantics(
            toolCall.Name,
            null,
            null,
            false,
            null,
            false,
            timeout,
            path,
            ToolArgumentHelper.GetString(toolCall.Arguments, "Content"),
            ToolArgumentHelper.GetString(toolCall.Arguments, "OldString"),
            ToolArgumentHelper.GetString(toolCall.Arguments, "NewString"),
            ToolArgumentHelper.GetBoolStrict(toolCall.Arguments, "ReplaceAll"));
    }

    /// <summary>Builds the correction returned before an approval request.</summary>
    internal static string BuildSuggestion(string managedTemporaryDirectory)
        => "Tool execution deferred: use_managed_temporary_directory\n" +
           $"Managed temporary directory: '{managedTemporaryDirectory}'.";

    /// <summary>Builds the hint returned when the user denies the corrected retry.</summary>
    internal static string BuildDenialHint(string managedTemporaryDirectory)
        => $"Hint: Use the managed temporary directory '{managedTemporaryDirectory}' for disposable artifacts. " +
           "The shared platform temporary root remains outside the session's trusted scope.";
}
