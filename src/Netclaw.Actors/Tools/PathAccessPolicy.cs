// -----------------------------------------------------------------------
// <copyright file="PathAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Owns canonical path resolution and path access decisions for structured tools and reviewed shell diagnostics.
/// </summary>
internal sealed class PathAccessPolicy
{
    internal enum PathAccessFailure
    {
        InvalidInput,
        AccessDenied,
        MissingBase
    }

    internal enum FileOperation
    {
        Read,
        Write,
        Attach,
        DeclareProjectScope
    }

    internal sealed record PathAccessDecision
    {
        private PathAccessDecision(
            bool allowed,
            string canonicalPath,
            string error,
            PathAccessFailure? failure)
        {
            Allowed = allowed;
            CanonicalPath = canonicalPath;
            Error = error;
            Failure = failure;
        }

        public bool Allowed { get; }

        public string CanonicalPath { get; }

        public string Error { get; }

        public PathAccessFailure? Failure { get; }

        public static PathAccessDecision Allow(string canonicalPath)
            => new(true, canonicalPath, string.Empty, null);

        public static PathAccessDecision Deny(
            string error,
            PathAccessFailure failure,
            string canonicalPath = "")
            => new(false, canonicalPath, error, failure);
    }

    private readonly ToolAudienceProfileResolver _profileResolver;
    private readonly ToolPathPolicy _protectedPaths;
    private readonly Lazy<IReadOnlyList<string>> _cachedGlobalReadRoots;
    private readonly Lazy<string?> _cachedWorkspacesRoot;
    private readonly IReadOnlyList<string> _sessionRoots;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    // paths is required (not nullable): the workspaces/global-read roots are
    // sourced from it, and a null would silently drop them — the exact silent
    // fallback that let autonomous workspace access break unnoticed (#1493).
    public PathAccessPolicy(
        ToolConfig toolConfig,
        NetclawPaths paths,
        ToolPathPolicy protectedPaths)
    {
        _profileResolver = new ToolAudienceProfileResolver(toolConfig, paths);
        _protectedPaths = protectedPaths;
        _sessionRoots = new[]
            {
                paths.SessionsDirectory,
                paths.SessionLogsDirectory
            }
            .Select(PathUtility.Normalize)
            .Distinct(PathComparer)
            .ToArray();
        _cachedGlobalReadRoots = new Lazy<IReadOnlyList<string>>(() =>
            _profileResolver.ResolveGlobalReadRoots()
                .Select(PathUtility.Normalize)
                .Distinct(PathComparer)
                .ToArray());
        _cachedWorkspacesRoot = new Lazy<string?>(() =>
        {
            var workspaces = _profileResolver.ResolveWorkspacesDirectory();
            return string.IsNullOrWhiteSpace(workspaces) ? null : PathUtility.Normalize(workspaces);
        });
    }

    public PathAccessDecision Evaluate(
        string rawPath,
        ToolInvocationContext context,
        FileOperation operation)
    {
        var profileOperation = operation == FileOperation.DeclareProjectScope
            ? FileOperation.Read
            : operation;
        var allowInteractivePersonalReach = operation != FileOperation.DeclareProjectScope;
        if (!TryResolvePath(
                rawPath,
                context,
                profileOperation,
                out var canonicalPath,
                out var error,
                out var failure,
                allowInteractivePersonalReach))
        {
            return PathAccessDecision.Deny(
                error,
                failure ?? throw new InvalidOperationException("A denied path decision must include a failure."),
                canonicalPath);
        }

        return AllowIfUnprotected(canonicalPath, profileOperation);
    }

    /// <summary>
    /// Evaluates one parser-resolved input to a reviewed diagnostic command.
    /// Reviewed diagnostics are read-only and receive only the session and
    /// declared-project trusted roots; they do not inherit broader global read
    /// roots or the interactive Personal filesystem reach.
    /// </summary>
    public PathAccessDecision EvaluateReviewedDiagnosticRead(
        string canonicalPath,
        ToolInvocationContext context,
        ShellPathStyle pathStyle,
        string? proposedProjectRoot = null,
        bool includeRootInLinkCheck = true)
    {
        var profile = _profileResolver.ResolveProfile(context);
        if (profile.WriteFiles.Mode == ToolFilesystemMode.None)
        {
            return PathAccessDecision.Deny(
                "Error: Shell access is not allowed by this audience profile.",
                PathAccessFailure.AccessDenied,
                canonicalPath);
        }

        var roots = new List<string>();
        AddSessionRoots(roots, context);
        if (context.Audience != TrustAudience.Public)
        {
            if (!string.IsNullOrWhiteSpace(context.ProjectDirectory))
                roots.Add(context.ProjectDirectory);
            if (!string.IsNullOrWhiteSpace(proposedProjectRoot))
                roots.Add(proposedProjectRoot);
        }

        foreach (var root in roots.Distinct(PathComparer))
        {
            try
            {
                var normalizedPath = string.Empty;
                var normalizedRoot = string.Empty;
                var usesShellPathStyle = ShellPathRules.TryNormalize(canonicalPath, pathStyle, out normalizedPath)
                                         && ShellPathRules.TryNormalize(root, pathStyle, out normalizedRoot);
                if (usesShellPathStyle
                    && !ShellPathRules.IsWithinRoot(normalizedPath, normalizedRoot, pathStyle))
                {
                    continue;
                }

                if (!usesShellPathStyle)
                {
                    if (!Path.IsPathFullyQualified(canonicalPath)
                        || !Path.IsPathFullyQualified(root))
                    {
                        continue;
                    }

                    normalizedPath = PathUtility.Normalize(canonicalPath);
                    normalizedRoot = PathUtility.Normalize(root);
                    if (!PathUtility.IsNormalizedWithinRoot(normalizedPath, normalizedRoot))
                        continue;
                }

                if ((!usesShellPathStyle || ShellPathRules.UsesHostPathStyle(pathStyle))
                    && PathUtility.ContainsSymlinkSegment(
                        normalizedRoot,
                        normalizedPath,
                        includeRootInLinkCheck))
                {
                    return PathAccessDecision.Deny(
                        "Error: Path crosses a filesystem link inside a trusted root.",
                        PathAccessFailure.AccessDenied,
                        normalizedPath);
                }

                return AllowIfUnprotected(normalizedPath, FileOperation.Read);
            }
            catch (Exception ex) when (ex is ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                return PathAccessDecision.Deny(
                    "Error: Path relationship could not be verified.",
                    PathAccessFailure.AccessDenied,
                    canonicalPath);
            }
        }

        return PathAccessDecision.Deny(
            "Error: Path is outside trusted roots.",
            PathAccessFailure.AccessDenied,
            canonicalPath);
    }

    /// <summary>
    /// True when an interactive Personal-audience session gets shell-equivalent
    /// file reach: read and attach tools resolve outside the configured roots,
    /// matching the approval-gated shell surface. Unattended sessions, Team,
    /// and Public audiences are never granted this — their access remains
    /// bounded by trusted roots or fails closed.
    /// </summary>
    internal static bool HasInteractivePersonalReach(ToolInvocationContext context)
        => context.Audience == TrustAudience.Personal
           && context.RunScope.InteractiveApproval is InteractiveApprovalCapability.Available;

    public IReadOnlyList<string> GetTrustedRoots(ToolInvocationContext context, FileOperation accessKind)
    {
        var profile = _profileResolver.ResolveProfile(context);
        var access = GetAccessProfile(profile, accessKind);
        if (access.Mode == ToolFilesystemMode.None)
            return [];

        if (access.Mode == ToolFilesystemMode.All)
            return ResolveUnattendedTrustedRoots(context, accessKind);

        return ResolveAndMergeRoots(access, context, context.Audience, accessKind);
    }

    private bool TryResolvePath(
        string rawPath,
        ToolInvocationContext context,
        FileOperation accessKind,
        out string fullPath,
        out string error,
        out PathAccessFailure? failure,
        bool allowInteractivePersonalReach = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Any(char.IsControl))
            {
                fullPath = string.Empty;
                error = "Error: Invalid path.";
                failure = PathAccessFailure.InvalidInput;
                return false;
            }

            if (Path.IsPathFullyQualified(rawPath))
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            else if (Path.IsPathRooted(rawPath))
            {
                fullPath = string.Empty;
                error = "Error: Invalid path: partially qualified paths are not supported.";
                failure = PathAccessFailure.InvalidInput;
                return false;
            }
            else
            {
                var baseResult = TryGetRelativePathBase(context, accessKind, out var baseDirectory);
                if (baseResult == PathBaseStatus.Resolved)
                {
                    fullPath = Path.GetFullPath(rawPath, baseDirectory);
                }
                else
                {
                    fullPath = string.Empty;
                    if (baseResult == PathBaseStatus.Denied)
                    {
                        error = "Error: The project or session directory contains an unsafe filesystem link.";
                        failure = PathAccessFailure.AccessDenied;
                    }
                    else
                    {
                        error = "Error: invalid_context: No project or session directory is available.";
                        failure = PathAccessFailure.MissingBase;
                    }

                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            error = $"Error: Invalid path: {ex.Message}";
            failure = PathAccessFailure.InvalidInput;
            return false;
        }

        var profile = _profileResolver.ResolveProfile(context);
        var access = GetAccessProfile(profile, accessKind);
        var audience = context.Audience;

        if (access.Mode == ToolFilesystemMode.All)
        {
            // Unattended channels have no human approval backstop, so an
            // unrestricted audience is confined to trusted roots (session,
            // project, and operator-configured roots) instead of being
            // granted blanket filesystem access. Interactive channels keep the
            // blanket grant — the live approval gate is their backstop. This is the
            // single seam that covers shell and every structured file tool.
            // Project-scope declarations opt out of interactive Personal reach.
            // They stay confined to trusted roots even for default
            // Mode.All profiles: its declaration supplies the project directory
            // to reviewed-safe policy and feeds project identity files into the prompt.
            if (!allowInteractivePersonalReach
                || context.RunScope.InteractiveApproval is InteractiveApprovalCapability.Unavailable)
            {
                var allowed = TryResolveWithinTrustedRoots(fullPath, context, accessKind, out error);
                failure = allowed ? null : PathAccessFailure.AccessDenied;
                return allowed;
            }

            error = string.Empty;
            failure = null;
            return true;
        }

        var label = GetAudienceLabel(audience);

        if (access.Mode == ToolFilesystemMode.None)
        {
            error = $"Error: {label} trust context does not allow {accessKind.ToString().ToLowerInvariant()} access to local files.";
            failure = PathAccessFailure.AccessDenied;
            return false;
        }

        // Interactive Personal-audience reads are shell-equivalent: shell reaches
        // any path in an interactive session (approval gate + ToolPathPolicy hard
        // deny), so read/attach tools do too. This kills the shell-workaround
        // (cat, cp-into-session) for legitimate out-of-roots files. The hard deny
        // surface still applies inside the tools via ToolPathPolicy.IsReadDenied
        // (file_read, file_list, attach_file), and autonomous sessions never reach
        // this branch — InteractiveApproval is Unavailable there, so path access
        // stays within trusted roots or fails closed below. Project-scope
        // declarations opt out because their result feeds reviewed-safe policy.
        if (allowInteractivePersonalReach
            && accessKind is (FileOperation.Read or FileOperation.Attach)
            && HasInteractivePersonalReach(context))
        {
            error = string.Empty;
            failure = null;
            return true;
        }

        var roots = ResolveAndMergeRoots(access, context, audience, accessKind);

        if (roots.Count == 0)
        {
            error = $"Error: {label} trust context does not have any configured local file roots for {accessKind.ToString().ToLowerInvariant()} access.";
            failure = PathAccessFailure.AccessDenied;
            return false;
        }

        var relationship = GetHostPathRelationship(fullPath, roots);
        if (relationship == PathRelationship.WithinTrustedRoot)
        {
            error = string.Empty;
            failure = null;
            return true;
        }

        if (relationship is PathRelationship.CrossesLinkBoundary or PathRelationship.Unverifiable)
        {
            error = $"Error: {label} trust context may not access files through symlinked paths inside the current session directory or configured roots.";
            failure = PathAccessFailure.AccessDenied;
            return false;
        }

        error = audience == TrustAudience.Public
            ? $"Error: {label} trust context may only access files inside the current session directory."
            : $"Error: {label} trust context may only access files inside the current session directory or configured roots: {string.Join(", ", roots)}.";
        failure = PathAccessFailure.AccessDenied;
        return false;
    }

    private PathBaseStatus TryGetRelativePathBase(
        ToolInvocationContext context,
        FileOperation accessKind,
        out string baseDirectory)
    {
        var projectResult = TryNormalizeAbsoluteBase(
            context.ProjectDirectory,
            requireExistingDirectory: true,
            out baseDirectory);
        if (projectResult == PathBaseStatus.Resolved)
        {
            var relationship = GetPathRelationship(baseDirectory, context, accessKind);
            if (relationship == PathRelationship.WithinTrustedRoot
                || (relationship == PathRelationship.OutsideTrustedRoots
                    && HasInteractivePersonalReach(context)))
            {
                return PathBaseStatus.Resolved;
            }

            baseDirectory = string.Empty;
            return PathBaseStatus.Denied;
        }

        if (projectResult == PathBaseStatus.Denied)
            return PathBaseStatus.Denied;

        return TryNormalizeAbsoluteBase(context.SessionDirectory, requireExistingDirectory: false, out baseDirectory);
    }

    private PathRelationship GetPathRelationship(
        string projectDirectory,
        ToolInvocationContext context,
        FileOperation accessKind)
    {
        var roots = new List<string>();
        AddSessionRoots(roots, context);

        var profile = _profileResolver.ResolveProfile(context);
        roots.AddRange(_profileResolver.ResolveRoots(profile.ReadFiles, context));
        roots.AddRange(_cachedGlobalReadRoots.Value);
        if (accessKind is not FileOperation.Read && _cachedWorkspacesRoot.Value is { } workspacesRoot)
            roots.Add(workspacesRoot);

        return GetHostPathRelationship(projectDirectory, roots);
    }

    private static PathBaseStatus TryNormalizeAbsoluteBase(
        string? candidate,
        bool requireExistingDirectory,
        out string baseDirectory)
    {
        baseDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Any(char.IsControl)
            || !Path.IsPathFullyQualified(candidate))
            return PathBaseStatus.Unavailable;

        try
        {
            var normalized = Path.GetFullPath(candidate);
            if (requireExistingDirectory && !Directory.Exists(normalized))
                return PathBaseStatus.Unavailable;
            if (Directory.Exists(normalized)
                && (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            {
                return PathBaseStatus.Denied;
            }

            baseDirectory = normalized;
            return PathBaseStatus.Resolved;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return PathBaseStatus.Denied;
        }
    }

    private enum PathBaseStatus
    {
        Unavailable,
        Resolved,
        Denied
    }

    private enum PathRelationship
    {
        OutsideTrustedRoots,
        WithinTrustedRoot,
        CrossesLinkBoundary,
        Unverifiable
    }

    private static ToolFilesystemAccessProfile GetAccessProfile(ToolAudienceProfile profile, FileOperation accessKind) =>
        accessKind switch
        {
            FileOperation.Read => profile.ReadFiles,
            FileOperation.Write => profile.WriteFiles,
            FileOperation.Attach => profile.AttachFiles,
            _ => profile.ReadFiles
        };

    /// <summary>
    /// Resolves profile roots and merges global read roots for read access.
    /// Single source of truth for root resolution — used by both
    /// <see cref="GetTrustedRoots"/> and <see cref="TryResolvePath"/>.
    /// Public audience is excluded from global read roots (skills, identity,
    /// workspaces) — it may only access the shared session trusted roots.
    /// </summary>
    private IReadOnlyList<string> ResolveAndMergeRoots(
        ToolFilesystemAccessProfile access,
        ToolInvocationContext context,
        TrustAudience audience,
        FileOperation accessKind)
    {
        var roots = _profileResolver.ResolveRoots(access, context)
            .Select(PathUtility.Normalize)
            .ToList();

        AddSessionRoots(roots, context);

        if (accessKind == FileOperation.Read && audience != TrustAudience.Public)
        {
            foreach (var globalRoot in _cachedGlobalReadRoots.Value)
                roots.Add(globalRoot);
        }

        return roots.Distinct(PathComparer).ToArray();
    }

    /// <summary>
    /// Confines an unattended session whose audience would otherwise grant
    /// unrestricted (<see cref="ToolFilesystemMode.All"/>) access to its
    /// trusted roots. Fails closed when no trusted root is available.
    /// </summary>
    private bool TryResolveWithinTrustedRoots(
        string fullPath,
        ToolInvocationContext context,
        FileOperation accessKind,
        out string error)
    {
        var roots = ResolveUnattendedTrustedRoots(context, accessKind);
        if (roots.Count == 0)
        {
            error = "Error: unattended session has no trusted file roots.";
            return false;
        }

        var relationship = GetHostPathRelationship(fullPath, roots);
        if (relationship == PathRelationship.WithinTrustedRoot)
        {
            error = string.Empty;
            return true;
        }

        if (relationship is PathRelationship.CrossesLinkBoundary or PathRelationship.Unverifiable)
        {
            error = "Error: unattended session may not access files through links inside trusted roots.";
            return false;
        }

        error = "Error: unattended session may only access files inside trusted roots.";
        return false;
    }

    private static PathRelationship GetHostPathRelationship(
        string fullPath,
        IEnumerable<string> roots)
    {
        foreach (var candidate in roots)
        {
            try
            {
                var root = Path.GetFullPath(candidate);
                if (!PathUtility.IsWithinRoot(fullPath, root))
                    continue;

                return PathUtility.ContainsSymlinkSegment(root, fullPath, includeRoot: true)
                    ? PathRelationship.CrossesLinkBoundary
                    : PathRelationship.WithinTrustedRoot;
            }
            catch (Exception ex) when (ex is ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or PathTooLongException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                return PathRelationship.Unverifiable;
            }
        }

        return PathRelationship.OutsideTrustedRoots;
    }

    /// <summary>
    /// Resolves trusted roots for an unattended invocation from the shared
    /// Netclaw session roots and current project
    /// directory, available for both reads and writes. Read access
    /// additionally includes the non-sensitive global read roots (skills,
    /// identity, workspaces). Write/attach access additionally includes the
    /// configured <em>workspaces</em> directory only — the operator's designated
    /// writable working area — but NOT skills/identity, which are system-managed
    /// (an unattended session must never rewrite its own identity or skills).
    /// Plain file writes are not gated by the interactive approval system, so
    /// confining them to only the current session and project blocked legitimate cross-run state in
    /// the workspace without a security benefit. No additional plumbing — the
    /// cached read roots and workspaces root already exist on this policy.
    /// </summary>
    private IReadOnlyList<string> ResolveUnattendedTrustedRoots(ToolInvocationContext context, FileOperation accessKind)
    {
        var roots = new List<string>();

        AddSessionRoots(roots, context);

        if (!string.IsNullOrWhiteSpace(context.ProjectDirectory))
            roots.Add(context.ProjectDirectory);

        if (accessKind == FileOperation.Read)
            roots.AddRange(_cachedGlobalReadRoots.Value);
        else if (_cachedWorkspacesRoot.Value is { } workspacesRoot)
            roots.Add(workspacesRoot);

        return roots
            .Select(PathUtility.Normalize)
            .Distinct(PathComparer)
            .ToArray();
    }

    private void AddSessionRoots(List<string> roots, ToolInvocationContext context)
    {
        roots.AddRange(_sessionRoots);

        if (context.SessionStorage?.Binding is { } binding)
            roots.Add(binding.EnvelopeRoot.Value);

        // Preserve access for legacy and already-running sessions whose bound
        // directory predates the shared session-root layout.
        if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
            roots.Add(context.SessionDirectory);
    }

    private bool IsProtected(string path, FileOperation operation)
        => operation == FileOperation.Write
            ? _protectedPaths.IsDenied(path)
            : _protectedPaths.IsReadDenied(path);

    private PathAccessDecision AllowIfUnprotected(string canonicalPath, FileOperation operation)
    {
        if (!IsProtected(canonicalPath, operation))
            return PathAccessDecision.Allow(canonicalPath);

        var error = operation == FileOperation.Write
            ? FileToolErrors.ControlPlaneWriteDenied(canonicalPath)
            : FileToolErrors.CredentialReadDenied(canonicalPath);
        return PathAccessDecision.Deny(error, PathAccessFailure.AccessDenied, canonicalPath);
    }

    private static string GetAudienceLabel(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => "Public",
        TrustAudience.Team => "Team",
        TrustAudience.Personal => "Personal",
        _ => "Public"
    };

}
