// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginManagementService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>Owns plugin source validation, reference resolution, and operator views.</summary>
internal sealed class GitSkillPluginManagementService(
    SkillFeedsConfig feedsConfig,
    NetclawPaths paths,
    IGitSkillPluginAcquirer acquirer,
    GitSkillPluginStateStore stateStore,
    GitSkillPluginConfigStore configStore,
    TimeProvider timeProvider)
{
    public async Task<GitSkillPluginSource> InstallAsync(
        GitSkillPluginApi.InstallRequest request,
        CancellationToken cancellationToken)
    {
        var repository = NormalizeRepository(request.Repository);
        var name = NormalizeName(request.Name, repository);
        var subdirectory = NormalizeSubdirectory(request.Subdirectory);
        ValidateInstallRequest(request);

        if (feedsConfig.Plugins.Any(
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Conflict,
                $"Plugin '{name}' already exists.");
        }

        if (feedsConfig.Plugins.Count >= GitSkillPluginSourceValidator.MaximumSourceCount)
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Conflict,
                $"No more than {GitSkillPluginSourceValidator.MaximumSourceCount} GitHub plugins can be configured.");
        }

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(request.TimeoutSeconds), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            var source = await ResolveSourceAsync(
                request,
                name,
                repository,
                subdirectory,
                linked.Token);
            configStore.Add(source);
            return source;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The GitHub plugin request exceeded its configured timeout.");
        }
    }

    public async Task<GitSkillPluginApi.ListResponse> ListAsync(
        CancellationToken cancellationToken)
    {
        var receipts = (await stateStore.LoadReceiptsAsync(cancellationToken))
            .ToDictionary(receipt => receipt.SourceName, StringComparer.OrdinalIgnoreCase);
        var rows = feedsConfig.Plugins.Select(source =>
        {
            receipts.TryGetValue(source.Name, out var receipt);
            var installedDirectoryExists = receipt is not null
                && Directory.Exists(paths.ManagedGitSkillCommitDirectory(
                    receipt.SourceName,
                    receipt.SourceFingerprint,
                    receipt.InstalledCommit));
            var status = !source.Enabled
                ? GitSkillPluginApi.PluginStatus.Disabled
                : receipt is null || !installedDirectoryExists
                    ? GitSkillPluginApi.PluginStatus.NotInstalled
                    : string.Equals(
                        receipt.SourceFingerprint,
                        GitSkillPluginSourceValidator.Fingerprint(source),
                        StringComparison.Ordinal)
                        ? GitSkillPluginApi.PluginStatus.Installed
                        : GitSkillPluginApi.PluginStatus.Stale;

            return ToRow(source, status, receipt);
        }).ToList();

        return new GitSkillPluginApi.ListResponse { Plugins = rows };
    }

    public bool SetEnabled(string name, bool enabled)
    {
        ValidateName(name);
        return configStore.SetEnabled(name, enabled);
    }

    public bool Remove(string name)
    {
        ValidateName(name);
        return configStore.Remove(name);
    }

    private async Task<GitSkillPluginSource> ResolveSourceAsync(
        GitSkillPluginApi.InstallRequest request,
        string name,
        string repository,
        string? subdirectory,
        CancellationToken cancellationToken)
    {
        var referenceKind = request.ReferenceKind;
        var reference = request.Reference;
        if (referenceKind == GitSkillPluginApi.InstallReferenceKind.DefaultBranch)
        {
            reference = await acquirer.ResolveDefaultBranchAsync(repository, cancellationToken);
            referenceKind = GitSkillPluginApi.InstallReferenceKind.Branch;
        }
        else if (referenceKind == GitSkillPluginApi.InstallReferenceKind.Commit)
        {
            reference = reference!.ToLowerInvariant();
        }

        var source = NewSource(
            name,
            repository,
            request.Format,
            subdirectory,
            referenceKind == GitSkillPluginApi.InstallReferenceKind.Commit
                ? GitSkillPluginReferenceKind.Commit
                : GitSkillPluginReferenceKind.Branch,
            reference!,
            request.TimeoutSeconds);

        if (referenceKind == GitSkillPluginApi.InstallReferenceKind.Tag)
        {
            var commit = await acquirer.ResolveCommitAsync(source, cancellationToken);
            source = NewSource(
                name,
                repository,
                request.Format,
                subdirectory,
                GitSkillPluginReferenceKind.Commit,
                commit,
                request.TimeoutSeconds);
        }

        if (!GitSkillPluginSourceValidator.TryValidateSource(source, out var error))
            throw new InvalidOperationException(error);

        return source;
    }

    private static GitSkillPluginSource NewSource(
        string name,
        string repository,
        string format,
        string? subdirectory,
        GitSkillPluginReferenceKind referenceKind,
        string reference,
        int timeoutSeconds) => new()
        {
            Name = name,
            Repository = repository,
            Format = format,
            Subdirectory = subdirectory,
            ReferenceKind = referenceKind,
            Reference = reference,
            Enabled = true,
            TimeoutSeconds = timeoutSeconds,
        };

    internal static GitSkillPluginApi.PluginRow ToRow(
        GitSkillPluginSource source,
        GitSkillPluginApi.PluginStatus status,
        GitSkillPluginReceipt? receipt) => new()
        {
            Name = source.Name,
            Repository = source.Repository,
            Format = source.Format,
            Subdirectory = source.Subdirectory,
            ReferenceKind = source.ReferenceKind,
            Reference = source.Reference,
            Enabled = source.Enabled,
            Status = status,
            InstalledCommit = receipt?.InstalledCommit,
            LastObservedCommit = receipt?.LastObservedCommit,
            InstalledVersion = receipt?.InstalledVersion,
        };

    private static string NormalizeRepository(string value)
    {
        if (!GitSkillPluginSourceValidator.TryNormalizeRepository(value, out var repository, out var error))
            throw new InvalidOperationException(error);
        return repository;
    }

    private static string NormalizeName(string? value, string repository)
    {
        var name = value ?? DeriveName(repository[(repository.IndexOf('/', StringComparison.Ordinal) + 1)..]);
        ValidateName(name);
        return name;
    }

    private static void ValidateName(string name)
    {
        if (!GitSkillPluginSourceValidator.TryValidateName(name, out var error))
            throw new InvalidOperationException(error);
    }

    private static string? NormalizeSubdirectory(string? value)
    {
        if (!GitSkillPluginSourceValidator.TryNormalizeRelativePath(
                value,
                allowEmpty: true,
                out var path,
                out var error))
        {
            throw new InvalidOperationException(error);
        }
        return path;
    }

    private static void ValidateInstallRequest(GitSkillPluginApi.InstallRequest request)
    {
        if (!string.Equals(request.Format, "codex", StringComparison.Ordinal))
            throw new InvalidOperationException("The plugin format must be 'codex'.");
        if (!Enum.IsDefined(request.ReferenceKind))
            throw new InvalidOperationException("The plugin reference type is not supported.");
        if (request.TimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("The plugin timeout must be from 1 through 300 seconds.");

        if (request.ReferenceKind == GitSkillPluginApi.InstallReferenceKind.DefaultBranch)
        {
            if (request.Reference is not null)
                throw new InvalidOperationException("The default branch request cannot include a reference.");
            return;
        }

        if (request.Reference is null)
            throw new InvalidOperationException("The reference is required.");

        var kind = request.ReferenceKind == GitSkillPluginApi.InstallReferenceKind.Commit
            ? GitSkillPluginReferenceKind.Commit
            : GitSkillPluginReferenceKind.Branch;
        if (!GitSkillPluginSourceValidator.TryValidateReference(kind, request.Reference, out var error))
            throw new InvalidOperationException(error);
    }

    private static string DeriveName(string repository)
    {
        var characters = repository
            .Select(character => char.IsAsciiLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '-')
            .ToArray();
        var words = new string(characters)
            .Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('-', words);
    }
}
