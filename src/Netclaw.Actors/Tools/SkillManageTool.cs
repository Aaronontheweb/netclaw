// -----------------------------------------------------------------------
// <copyright file="SkillManageTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text.RegularExpressions;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// CRUD tool for skill creation and management. Validates AgentSkills.io format,
/// writes atomically, and triggers registry re-scan after mutations.
/// </summary>
[NetclawTool("skill_manage",
    "Create, edit, patch, or delete skills and their resource files. "
    + "Actions: create, edit, patch, delete, write_file, remove_file.",
    Grant = "builtin")]
[ToolArgumentVariant("Action", "create", Required = ["Content"], Forbidden = ["FilePath", "FileContent", "OldString", "NewString", "ReplaceAll"])]
[ToolArgumentVariant("Action", "edit", Required = ["Content"], Forbidden = ["FilePath", "FileContent", "OldString", "NewString", "ReplaceAll"])]
[ToolArgumentVariant("Action", "patch", Required = ["OldString", "NewString"], Forbidden = ["Content", "FileContent"])]
[ToolArgumentVariant("Action", "delete", Forbidden = ["Content", "FilePath", "FileContent", "OldString", "NewString", "ReplaceAll"])]
[ToolArgumentVariant("Action", "write_file", Required = ["FilePath", "FileContent"], Forbidden = ["Content", "OldString", "NewString", "ReplaceAll"])]
[ToolArgumentVariant("Action", "remove_file", Required = ["FilePath"], Forbidden = ["Content", "FileContent", "OldString", "NewString", "ReplaceAll"])]
public sealed partial class SkillManageTool : NetclawTool<SkillManageTool.Params>
{
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex ValidNameRegex();

    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;
    private const string AtomicTempSuffix = ".tmp";
    private const string LinkDeniedMessage = "Symlink traversal is not allowed in skill file paths.";

    private readonly SkillRegistry _skillRegistry;
    private readonly NetclawPaths _paths;
    private readonly ISkillContentScanner _scanner;
    private readonly SkillInventoryRefresher _inventoryRefresher;
    private readonly ToolPathPolicy _protectedPaths;

    /// <summary>
    /// Test seam. Tests use it to change the file system at an exact stage of a mutation.
    /// Production code does not set it.
    /// </summary>
    internal Action<SkillMutationStage>? StageHookForTesting { get; init; }

    public record Params(
        [property: Description("Action to perform: create, edit, patch, delete, write_file, remove_file")]
        string Action,
        [property: Description("Skill name (lowercase letters, numbers, hyphens)")]
        string Name,
        [property: Description("Full SKILL.md content for create/edit actions")]
        string? Content = null,
        [property: Description("Relative file path within the skill for write_file/remove_file/patch")]
        string? FilePath = null,
        [property: Description("File content for write_file action")]
        string? FileContent = null,
        [property: Description("String to find for patch action")]
        string? OldString = null,
        [property: Description("Replacement string for patch action")]
        string? NewString = null,
        [property: Description("Replace all occurrences for patch action (default: false)")]
        bool ReplaceAll = false);

    public SkillManageTool(
        SkillRegistry skillRegistry,
        NetclawPaths paths,
        ISkillContentScanner scanner,
        SkillInventoryRefresher inventoryRefresher,
        ToolPathPolicy protectedPaths)
    {
        _skillRegistry = skillRegistry;
        _paths = paths;
        _scanner = scanner;
        _inventoryRefresher = inventoryRefresher;
        _protectedPaths = protectedPaths ?? throw new ArgumentNullException(nameof(protectedPaths));
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        var action = args.Action.Trim().ToLowerInvariant();
        try
        {
            return action switch
            {
                "create" => await CreateAsync(args, ct),
                "edit" => await EditAsync(args, ct),
                "patch" => await PatchAsync(args, ct),
                "delete" => Delete(args),
                "write_file" => await WriteFileAsync(args, ct),
                "remove_file" => RemoveFile(args),
                _ => $"Unknown action '{action}'. Valid actions: create, edit, patch, delete, write_file, remove_file."
            };
        }
        catch (UnsafePathException)
        {
            // The use-time check found a link that appeared after GuardMutationTarget.
            return LinkDeniedMessage;
        }
        catch (PlatformNotSupportedException)
        {
            return "skill_manage cannot change skill files safely on this platform. The operation was not done.";
        }
    }

    private async Task<string> CreateAsync(Params args, CancellationToken ct)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.Content))
            return "Content is required for create action.";

        var name = args.Name.Trim().ToLowerInvariant();

        // Reject writes to system directory
        if (IsSystemSkill(name))
            return "Cannot create skills in the .system directory. System skills are read-only.";

        var contentError = ValidateFrontmatter(args.Content);
        if (contentError is not null) return contentError;

        var identityError = ValidateManagedIdentity(name, args.Content);
        if (identityError is not null) return identityError;

        var scanResult = await _scanner.ScanAsync(name, args.Content, ct);
        if (!scanResult.IsAllowed)
            return $"Content scan rejected: {scanResult.Reason}";

        var skillDir = Path.Combine(_paths.SkillsDirectory, name);
        var skillPath = Path.Combine(skillDir, "SKILL.md");

        var targetError = GuardMutationTarget(skillDir, skillPath, atomicWrite: true);
        if (targetError is not null) return targetError;

        if (File.Exists(skillPath))
        {
            // If the file exists on disk but isn't in the registry (orphaned from file_write),
            // allow create to overwrite it. Only block if the skill is properly registered.
            var existing = FindSkill(name);
            if (existing is not null)
                return $"Skill '{name}' already exists. Use 'edit' to modify it.";

            // Orphaned file — overwrite and register it
            StageHookForTesting?.Invoke(SkillMutationStage.Checked);
            AtomicWrite(skillPath, args.Content, createDirectory: true);
            RescanAndUpdateIndex();
            return $"Skill '{name}' created at {skillDir} (replaced orphaned file)";
        }

        StageHookForTesting?.Invoke(SkillMutationStage.Checked);
        AtomicWrite(skillPath, args.Content, createDirectory: true);
        var rescan = RescanAndUpdateIndex();

        var message = $"Skill '{name}' created at {skillDir}";
        if (scanResult.Verdict == ScanVerdict.Warning)
            message += $" (warning: {scanResult.Reason})";

        return AppendScanWarnings(message, rescan);
    }

    private async Task<string> EditAsync(Params args, CancellationToken ct)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.Content))
            return "Content is required for edit action.";

        var name = args.Name.Trim().ToLowerInvariant();

        if (IsSystemSkill(name))
            return "Cannot edit skills in the .system directory. System skills are read-only.";

        var skill = FindSkill(name);
        if (skill is null)
        {
            // The skill might exist on disk but not be in the registry (orphaned from file_write).
            // Rescan and retry before giving up.
            var candidatePath = Path.Combine(_paths.SkillsDirectory, name, "SKILL.md");
            if (File.Exists(candidatePath))
            {
                RescanAndUpdateIndex();
                skill = FindSkill(name);
            }

            if (skill is null)
                return $"Skill '{name}' not found.";
        }

        var readOnlyError = GuardReadOnly(skill, "edit");
        if (readOnlyError is not null) return readOnlyError;

        var contentError = ValidateFrontmatter(args.Content);
        if (contentError is not null) return contentError;

        var identityError = ValidateManagedIdentity(name, args.Content);
        if (identityError is not null) return identityError;

        var targetError = GuardMutationTarget(skill.SkillDirectory, skill.FilePath, atomicWrite: true);
        if (targetError is not null) return targetError;

        var scanResult = await _scanner.ScanAsync(name, args.Content, ct);
        if (!scanResult.IsAllowed)
            return $"Content scan rejected: {scanResult.Reason}";

        StageHookForTesting?.Invoke(SkillMutationStage.Checked);
        AtomicWrite(skill.FilePath, args.Content, createDirectory: false);
        var rescan = RescanAndUpdateIndex();

        var message = $"Skill '{name}' updated.";
        if (scanResult.Verdict == ScanVerdict.Warning)
            message += $" (warning: {scanResult.Reason})";

        return AppendScanWarnings(message, rescan);
    }

    private async Task<string> PatchAsync(Params args, CancellationToken ct)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.OldString))
            return "OldString is required for patch action.";
        if (args.NewString is null)
            return "NewString is required for patch action.";

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = FindSkill(name);
        if (skill is null)
            return $"Skill '{name}' not found.";

        var readOnlyError = GuardReadOnly(skill, "patch");
        if (readOnlyError is not null) return readOnlyError;

        // Determine target file
        var targetPath = skill.FilePath;
        if (!string.IsNullOrWhiteSpace(args.FilePath))
        {
            if (!SkillResourcePath.TryNormalize(args.FilePath, out var normalizedPath, out var fileError))
                return SkillResourcePath.FormatManageError(fileError);
            targetPath = Path.Combine(skill.SkillDirectory, normalizedPath);
        }

        // Check before the read: a link could otherwise copy an outside file into the skill.
        var targetError = GuardMutationTarget(skill.SkillDirectory, targetPath, atomicWrite: true);
        if (targetError is not null) return targetError;

        StageHookForTesting?.Invoke(SkillMutationStage.Checked);
        if (!File.Exists(targetPath))
            return $"File not found: {args.FilePath ?? "SKILL.md"}";

        // One directory handle serves the read and the write, also across the scan.
        using var directory = OpenParentDirectory(targetPath, createMissing: false);
        if (directory is null)
            return $"File not found: {args.FilePath ?? "SKILL.md"}";
        StageHookForTesting?.Invoke(SkillMutationStage.DirectoryOpened);

        var targetName = Path.GetFileName(targetPath);
        var content = directory.ReadAllText(targetName);
        if (content is null)
            return $"File not found: {args.FilePath ?? "SKILL.md"}";

        var occurrences = CountOccurrences(content, args.OldString);

        if (occurrences == 0)
            return "OldString not found in the file.";

        if (occurrences > 1 && !args.ReplaceAll)
            return $"OldString found {occurrences} times. Set ReplaceAll=true to replace all, or provide a more specific string.";

        var newContent = args.ReplaceAll
            ? content.Replace(args.OldString, args.NewString, StringComparison.Ordinal)
            : ReplaceFirst(content, args.OldString, args.NewString);

        if (targetPath == skill.FilePath)
        {
            var contentError = ValidateFrontmatter(newContent);
            if (contentError is not null) return contentError;

            var identityError = ValidateManagedIdentity(name, newContent);
            if (identityError is not null) return identityError;
        }

        var scanSubject = targetPath == skill.FilePath
            ? name
            : $"{name}:{Path.GetRelativePath(skill.SkillDirectory, targetPath).Replace(Path.DirectorySeparatorChar, '/')}";
        var scanResult = await _scanner.ScanAsync(scanSubject, newContent, ct);
        if (!scanResult.IsAllowed)
            return $"Content scan rejected: {scanResult.Reason}";

        if (targetPath == skill.FilePath)
        {
            directory.WriteAllTextAtomic(targetName, newContent, targetName + AtomicTempSuffix);

            var message = "Patch applied.";
            if (scanResult.Verdict == ScanVerdict.Warning)
                message += $" (warning: {scanResult.Reason})";

            return AppendScanWarnings(message, RescanAndUpdateIndex());
        }

        directory.WriteAllTextAtomic(targetName, newContent, targetName + AtomicTempSuffix);
        var warning = scanResult.Verdict == ScanVerdict.Warning
            ? $" (warning: {scanResult.Reason})"
            : string.Empty;
        return $"Patch applied.{warning}";
    }

    private string Delete(Params args)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = FindSkill(name);
        if (skill is null)
            return $"Skill '{name}' not found.";

        var readOnlyError = GuardReadOnly(skill, "delete");
        if (readOnlyError is not null) return readOnlyError;

        var deleteTarget = skill.IsFlatFile ? skill.FilePath : skill.SkillDirectory;
        var targetError = GuardMutationTarget(skill.SkillDirectory, deleteTarget, atomicWrite: false);
        if (targetError is not null) return targetError;

        StageHookForTesting?.Invoke(SkillMutationStage.Checked);
        if (skill.IsFlatFile)
        {
            // Flat-file skill: delete the single .md file
            using var directory = OpenParentDirectory(skill.FilePath, createMissing: false)
                ?? throw new DirectoryNotFoundException($"The directory of skill '{name}' does not exist.");
            StageHookForTesting?.Invoke(SkillMutationStage.DirectoryOpened);
            directory.DeleteFile(Path.GetFileName(skill.FilePath));
        }
        else
        {
            using (var parentDirectory = OpenParentDirectory(skill.SkillDirectory, createMissing: false)
                       ?? throw new DirectoryNotFoundException($"The directory of skill '{name}' does not exist."))
            {
                StageHookForTesting?.Invoke(SkillMutationStage.DirectoryOpened);
                parentDirectory.DeleteTree(Path.GetFileName(skill.SkillDirectory));
            }

            // Clean empty parent category directories
            var parent = Path.GetDirectoryName(skill.SkillDirectory);
            if (parent is not null && parent != _paths.SkillsDirectory)
            {
                using var categoryParent = OpenParentDirectory(parent, createMissing: false);
                categoryParent?.DeleteEmptyDirectory(Path.GetFileName(parent));
            }
        }

        return AppendScanWarnings($"Skill '{name}' deleted.", RescanAndUpdateIndex());
    }

    private async Task<string> WriteFileAsync(Params args, CancellationToken ct)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.FilePath))
            return "FilePath is required for write_file action.";
        if (args.FileContent is null)
            return "FileContent is required for write_file action.";

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = FindSkill(name);
        if (skill is null)
            return $"Skill '{name}' not found.";

        var readOnlyError = GuardReadOnly(skill, "write files in");
        if (readOnlyError is not null) return readOnlyError;

        if (!SkillResourcePath.TryNormalize(args.FilePath, out var normalizedPath, out var fileError))
            return SkillResourcePath.FormatManageError(fileError);

        var fullPath = Path.GetFullPath(Path.Combine(skill.SkillDirectory, normalizedPath));
        var targetError = GuardMutationTarget(skill.SkillDirectory, fullPath, atomicWrite: true);
        if (targetError is not null) return targetError;

        var scanResult = await _scanner.ScanAsync(
            $"{name}:{normalizedPath}",
            args.FileContent,
            ct);
        if (!scanResult.IsAllowed)
            return $"Content scan rejected: {scanResult.Reason}";

        StageHookForTesting?.Invoke(SkillMutationStage.Checked);
        AtomicWrite(fullPath, args.FileContent, createDirectory: true);
        var rescan = RescanAndUpdateIndex();

        var message = $"File written: {normalizedPath}";
        if (scanResult.Verdict == ScanVerdict.Warning)
            message += $" (warning: {scanResult.Reason})";

        return AppendScanWarnings(message, rescan);
    }

    private string RemoveFile(Params args)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.FilePath))
            return "FilePath is required for remove_file action.";

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = FindSkill(name);
        if (skill is null)
            return $"Skill '{name}' not found.";

        var readOnlyError = GuardReadOnly(skill, "remove files from");
        if (readOnlyError is not null) return readOnlyError;

        if (!SkillResourcePath.TryNormalize(args.FilePath, out var normalizedPath, out var fileError))
            return SkillResourcePath.FormatManageError(fileError);

        var fullPath = Path.GetFullPath(Path.Combine(skill.SkillDirectory, normalizedPath));
        var targetError = GuardMutationTarget(skill.SkillDirectory, fullPath, atomicWrite: false);
        if (targetError is not null) return targetError;

        StageHookForTesting?.Invoke(SkillMutationStage.Checked);
        if (!File.Exists(fullPath))
            return $"File not found: {normalizedPath}";

        using (var directory = OpenParentDirectory(fullPath, createMissing: false))
        {
            if (directory is null)
                return $"File not found: {normalizedPath}";
            StageHookForTesting?.Invoke(SkillMutationStage.DirectoryOpened);
            if (!directory.DeleteFile(Path.GetFileName(fullPath)))
                return $"File not found: {normalizedPath}";
        }

        // Clean empty subdirectories
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && dir != skill.SkillDirectory)
        {
            using var parentDirectory = OpenParentDirectory(dir, createMissing: false);
            parentDirectory?.DeleteEmptyDirectory(Path.GetFileName(dir));
        }

        return AppendScanWarnings($"File removed: {normalizedPath}", RescanAndUpdateIndex());
    }

    // --- Helpers ---

    private SkillEntry? FindSkill(string name) =>
        _skillRegistry.GetAll()
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private bool IsSystemSkill(string name)
    {
        var skill = FindSkill(name);
        return skill is not null && IsSystemCategory(skill);
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Name is required.";

        var trimmed = name.Trim().ToLowerInvariant();
        if (trimmed.Length > MaxNameLength)
            return $"Name must be at most {MaxNameLength} characters.";

        if (!ValidNameRegex().IsMatch(trimmed))
            return "Name must contain only lowercase letters, numbers, and hyphens. Cannot start or end with a hyphen.";

        return null;
    }

    private static string? ValidateFrontmatter(string content)
    {
        var frontmatter = SkillScanner.ExtractFrontmatter(content);
        if (frontmatter is null)
            return "Content must start with YAML frontmatter (--- delimiters).";

        if (string.IsNullOrWhiteSpace(frontmatter.Description))
            return "Frontmatter must include a 'description' field.";

        if (frontmatter.Description.Length > MaxDescriptionLength)
            return $"Description must be at most {MaxDescriptionLength} characters.";

        return null;
    }

    private static string? ValidateManagedIdentity(string targetName, string content)
    {
        var frontmatter = SkillScanner.ExtractFrontmatter(content);
        if (frontmatter is null || string.IsNullOrWhiteSpace(frontmatter.Name))
            return null;

        var normalizedFrontmatterName = SkillScanner.NormalizeSkillName(frontmatter.Name);
        if (!string.Equals(normalizedFrontmatterName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Frontmatter name '{normalizedFrontmatterName}' does not match target skill '{targetName}'.";
        }

        return null;
    }

    private string? GuardReadOnly(SkillEntry skill, string verb)
    {
        if (skill.Source is not FileSkillSource)
            return $"Cannot {verb} remote skills. The source server owns this skill.";
        if (IsSystemCategory(skill))
            return $"Cannot {verb} system skills. System skills are read-only.";
        if (IsServerFeedSkill(skill))
            return $"Cannot {verb} server feed skills. Server feed skill directories are read-only.";
        if (IsExternalSkill(skill))
            return $"Cannot {verb} external skills. External skill directories are read-only.";
        return null;
    }

    private static bool IsSystemCategory(SkillEntry skill)
        => string.Equals(skill.Category, SkillScanner.SystemCategory, StringComparison.Ordinal);

    private bool IsServerFeedSkill(SkillEntry skill)
    {
        var feedRoot = PathUtility.Normalize(_paths.ServerFeedsDirectory);
        var skillPath = PathUtility.Normalize(Path.GetDirectoryName(skill.FilePath)!);
        return PathUtility.IsWithinRoot(skillPath, feedRoot);
    }

    private bool IsExternalSkill(SkillEntry skill)
    {
        var nativeRoot = PathUtility.Normalize(_paths.SkillsDirectory);
        var skillPath = PathUtility.Normalize(Path.GetDirectoryName(skill.FilePath)!);
        return !PathUtility.IsWithinRoot(skillPath, nativeRoot);
    }

    /// <summary>
    /// Returns an error when a skill mutation must not touch <paramref name="targetPath"/>.
    /// A text check alone is not sufficient. A link inside the native skills tree can
    /// send a write, patch, or delete to a file outside the skill, and a flat-file
    /// skill uses the skills root as its directory, so a relative path can reach the
    /// write-protected <c>.system</c> or <c>.server-feeds</c> tiers.
    /// </summary>
    /// <remarks>
    /// The link walk starts below the native skills root. The operator owns that
    /// root, and OS links above it (macOS <c>/var</c>) are not traversal. When
    /// <paramref name="atomicWrite"/> is true, the check also covers the
    /// <c>.tmp</c> file that <see cref="AtomicWrite"/> writes first.
    /// A failure to inspect the path denies the operation.
    /// <para>
    /// This check reads path names, and a link can appear after it. The file
    /// operations therefore do not trust it: they run through
    /// <see cref="SafeFileSystemMutation"/>, which checks each directory again at use
    /// time and does not follow a link. This check gives an early, clear denial and
    /// applies the protected-path policy.
    /// </para>
    /// </remarks>
    private string? GuardMutationTarget(string skillRoot, string targetPath, bool atomicWrite)
    {
        try
        {
            if (!PathUtility.IsWithinRoot(targetPath, skillRoot))
                return "Resolved path is outside the skill directory.";

            var paths = atomicWrite
                ? new[] { targetPath, targetPath + AtomicTempSuffix }
                : [targetPath];
            foreach (var path in paths)
            {
                if (PathUtility.ContainsSymlinkSegment(_paths.SkillsDirectory, path))
                    return LinkDeniedMessage;
            }

            foreach (var path in paths)
            {
                if (_protectedPaths.IsDenied(path))
                    return "The target path is protected. skill_manage cannot change it.";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return "Could not verify the target path. The operation was not done.";
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <c>path.tmp</c>, then renames it to
    /// <paramref name="path"/>. The operations are relative to a directory handle
    /// that <see cref="SafeFileSystemMutation"/> opened without link traversal, so a
    /// link that appears after <see cref="GuardMutationTarget"/> cannot redirect them.
    /// </summary>
    private void AtomicWrite(string path, string content, bool createDirectory)
    {
        using var directory = OpenParentDirectory(path, createDirectory)
            ?? throw new DirectoryNotFoundException("The skill directory does not exist.");
        StageHookForTesting?.Invoke(SkillMutationStage.DirectoryOpened);
        var name = Path.GetFileName(path);
        directory.WriteAllTextAtomic(name, content, name + AtomicTempSuffix);
    }

    /// <summary>
    /// Opens the directory that contains <paramref name="path"/>, one segment at a
    /// time below the native skills root, with no link traversal. Returns
    /// <c>null</c> when a directory is missing and <paramref name="createMissing"/>
    /// is false.
    /// </summary>
    private VerifiedDirectory? OpenParentDirectory(string path, bool createMissing)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The path has no parent directory.", nameof(path));
        // GuardMutationTarget keeps every target below the skills root. A ".." segment
        // here is rejected by SafeFileSystemMutation, so the operation fails closed.
        var relative = Path.GetRelativePath(_paths.SkillsDirectory, parent);
        return SafeFileSystemMutation.OpenDirectory(_paths.SkillsDirectory, relative, createMissing);
    }

    private Netclaw.Actors.Skills.SkillScanResult RescanAndUpdateIndex()
    {
        var mergedResult = _inventoryRefresher.Refresh();

        // Return as SkillScanResult for AppendScanWarnings compatibility
        return new Netclaw.Actors.Skills.SkillScanResult(mergedResult.AcceptedSkills, mergedResult.Issues);
    }

    private static string AppendScanWarnings(string message, Netclaw.Actors.Skills.SkillScanResult scanResult)
    {
        if (!scanResult.HasIssues)
            return message;

        return $"{message} Warning: skill inventory rebuild is degraded ({scanResult.Summary}). {string.Join(" ", scanResult.FormatIssueLines(3))}";
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }
}

/// <summary>
/// The stages of a <c>skill_manage</c> mutation that a test can intercept.
/// </summary>
internal enum SkillMutationStage
{
    /// <summary>The path checks passed. No file system change was done yet.</summary>
    Checked,

    /// <summary>
    /// The directory of the target is open and checked. The file operation is next.
    /// </summary>
    DirectoryOpened,
}
