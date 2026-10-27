// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Monitors <c>~/.netclaw/config/</c> for changes to <c>netclaw.json</c>.
/// Debounces file system events and validates new config before applying.
/// See SPEC-011 §Configuration Restart Coordination.
///
/// <para>
/// Single reload trigger: all config changes go to disk first (TUI wizard,
/// manual editing, agent self-configuration). This watcher is the only
/// mechanism that triggers config reload — there is no in-memory config
/// mutation path.
/// </para>
/// <para>
/// Every valid config change — including <see cref="DaemonConfig"/> properties
/// (bind address, exposure mode) — triggers a coordinated in-process restart via
/// <see cref="IDaemonRestartCoordinator"/>. That restart rebuilds the host and
/// re-binds Kestrel, so network settings take effect without an external restart.
/// </para>
/// </summary>
public sealed class ConfigWatcherService : IHostedService, IDisposable
{
    private readonly NetclawPaths _paths;
    private readonly ISecretsProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly IDaemonRestartCoordinator _restartCoordinator;
    private readonly RejectedConfigState _rejectedConfig;
    private readonly IReadOnlyList<ILlmProviderPlugin> _plugins;
    private readonly IModelCapabilityResolver _capabilities;
    private readonly ILoggerFactory _loggerFactory;
    private (string Reason, string Content)? _lastRejection;
    private readonly ILogger<ConfigWatcherService> _logger;
    private readonly IReadOnlyDictionary<string, string?> _lowerPriorityConfiguration;
    private readonly IReadOnlyDictionary<string, string?> _generationConfiguration;
    private readonly TaskCompletionSource _applicationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _applicationStartedRegistration;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(500);
    private readonly object _debounceLock = new();
    private Task _pendingReload = Task.CompletedTask;

    /// <summary>
    /// The in-flight debounced reload, or a completed task when none is pending.
    /// Lets tests await the reload deterministically instead of polling.
    /// </summary>
    internal Task PendingReload
    {
        get { lock (_debounceLock) { return _pendingReload; } }
    }

    /// <summary>The debounce window applied before a detected change is reloaded.</summary>
    internal TimeSpan DebounceInterval => _debounceInterval;

    public ConfigWatcherService(
        NetclawPaths paths,
        ISecretsProtector protector,
        TimeProvider timeProvider,
        IDaemonRestartCoordinator restartCoordinator,
        RejectedConfigState rejectedConfig,
        IEnumerable<ILlmProviderPlugin> plugins,
        IModelCapabilityResolver capabilities,
        ILoggerFactory loggerFactory,
        ILogger<ConfigWatcherService> logger,
        IHostApplicationLifetime applicationLifetime,
        IReadOnlyDictionary<string, string?> lowerPriorityConfiguration,
        IReadOnlyDictionary<string, string?> generationConfiguration)
    {
        _capabilities = capabilities;
        _loggerFactory = loggerFactory;
        _rejectedConfig = rejectedConfig;
        _plugins = plugins.ToList();
        _paths = paths;
        _protector = protector;
        _timeProvider = timeProvider;
        _restartCoordinator = restartCoordinator;
        _logger = logger;
        _lowerPriorityConfiguration = lowerPriorityConfiguration;
        _generationConfiguration = generationConfiguration;
        _applicationStartedRegistration = applicationLifetime.ApplicationStarted.Register(
            () => _applicationStarted.TrySetResult());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath);
        if (configDir is null)
        {
            _logger.LogWarning("Config path has no directory: {ConfigPath}. Hot-reload disabled.", _paths.NetclawConfigPath);
            return Task.CompletedTask;
        }

        // Ensure the directory exists so hot-reload is armed even when the daemon
        // started before any config was written (fresh container / first boot).
        // Otherwise a later `netclaw init` write would never be observed and the
        // wizard — which now relies on the watcher to apply config — would hang.
        try
        {
            Directory.CreateDirectory(configDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create config directory: {ConfigDir}. Hot-reload disabled.", configDir);
            return Task.CompletedTask;
        }

        _watcher = new FileSystemWatcher(configDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = false
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;

        lock (_debounceLock)
        {
            // An event after subscription must retain its ordinary reload behavior.
            _watcher.EnableRaisingEvents = true;
            ScheduleReload(startupReconciliation: true);
        }

        _logger.LogInformation("Config hot-reload watching: {ConfigDir}", configDir);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
        }
        _applicationStartedRegistration.Dispose();
        return Task.CompletedTask;
    }

    internal void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (!IsWatchedFile(e.Name))
                return;

            _logger.LogDebug("Config file changed: {FileName}", e.Name);
            ScheduleReload(startupReconciliation: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in config watcher Changed callback for {FileName}", e.Name);
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (!IsWatchedFile(e.Name))
                return;

            _logger.LogWarning("Config file deleted: {FileName}. Keeping current config.", e.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in config watcher Deleted callback for {FileName}", e.Name);
        }
    }

    internal void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            // Rename INTO our watched filename = atomic-replace write (write-temp + rename).
            if (IsWatchedFile(e.Name))
            {
                _logger.LogDebug("Config file rename detected: {OldName} -> {NewName}", e.OldName, e.Name);
                ScheduleReload(startupReconciliation: false);
                return;
            }

            // Rename OUT of our watched filename = treat like a delete.
            if (IsWatchedFile(e.OldName))
            {
                _logger.LogWarning("Config file renamed away: {OldName} -> {NewName}. Keeping current config.", e.OldName, e.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in config watcher Renamed callback for {FileName}", e.Name);
        }
    }

    private void ScheduleReload(bool startupReconciliation)
    {
        // FileSystemWatcher raises events on thread-pool threads and may deliver
        // them concurrently. The cancel-and-replace of the debounce CTS must be
        // atomic: without the lock a concurrent event can leave a debounce loop
        // awaiting a CTS that no later event will ever cancel, double-firing the
        // reload.
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            _pendingReload = RunDebouncedReloadAsync(startupReconciliation, _debounceCts.Token);
        }
    }

    private async Task RunDebouncedReloadAsync(bool startupReconciliation, CancellationToken token)
    {
        try
        {
            // Debounce on the injected TimeProvider so tests can virtualize it.
            await Task.Delay(_debounceInterval, _timeProvider, token);
            // A restart must not stop the host before its other services start.
            await _applicationStarted.Task.WaitAsync(token);
            if (token.IsCancellationRequested)
                return;

            await ApplyReloadAsync(startupReconciliation, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Config reload debounce cancelled by newer event");
        }
    }

    internal Task ApplyReloadAsync(CancellationToken cancellationToken) =>
        ApplyReloadAsync(startupReconciliation: false, cancellationToken);

    private async Task ApplyReloadAsync(bool startupReconciliation, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "[{Timestamp:o}] Config change detected, validating before restart...",
                _timeProvider.GetUtcNow());

            // A restart into a configuration that startup rejects would stop the daemon, so the
            // running daemon keeps its configuration and `netclaw status` says why.
            using var candidate = new ConfigurationManager();
            string? reason;
            var configurationChanged = true;
            try
            {
                var check = FindRejection(candidate);
                reason = check.Error;
                if (check.Valid is { } valid && startupReconciliation)
                {
                    var (generationValues, candidateValues) = PrepareComparison(candidate, valid.Providers);
                    configurationChanged = HasConfigurationChanged(generationValues, candidateValues);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Config check threw");
                reason = $"netclaw.json could not be checked: {FirstLine(ex.Message)}";
            }

            if (reason is not null)
            {
                Reject(reason);
                return;
            }

            _rejectedConfig.Clear();
            _lastRejection = null;

            if (startupReconciliation && !configurationChanged)
                return;

            // Every valid config change — including Daemon-section settings (bind
            // address, exposure mode) — is applied via the coordinated in-process
            // restart below. That restart rebuilds the host and re-binds Kestrel, so
            // network settings take effect without stopping/spawning the process.
            _logger.LogInformation("Config valid. Starting coordinated daemon restart.");
            await _restartCoordinator.RequestConfigRestartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config reload failed. Keeping previous config.");
        }
    }

    // The same checks startup makes, in the same order: the JSON syntax, the configuration
    // sources, the Models and Providers sections, and the provider plugins.
    private ModelConfigurationCheck FindRejection(ConfigurationManager configuration)
    {
        if (!ValidateConfigJson(_paths.NetclawConfigPath, out var jsonError))
            return new ModelConfigurationCheck($"netclaw.json is not valid JSON: {FirstLine(jsonError)}", null);

        try
        {
            configuration.AddInMemoryCollection(_lowerPriorityConfiguration);
            configuration.AddNetclawDaemonSources(_paths);
        }
        catch (InvalidDataException ex)
        {
            return new ModelConfigurationCheck(DaemonConfigurationSources.DescribeLoadFailure(ex), null);
        }

        var check = ModelConfigurationValidation.Check(configuration);
        if (check.Valid is not { } valid)
            return check;

        var error = valid.Validation.Status == ProviderRuntimeStatus.Valid
            ? new ProviderPluginFactory(valid.Providers, _plugins, _capabilities, _loggerFactory, _timeProvider).Validate(valid.Models)
            : null;
        return error is null ? check : new ModelConfigurationCheck(error, null);
    }

    private (Dictionary<string, string?> Generation, Dictionary<string, string?> Candidate) PrepareComparison(
        IConfiguration candidateConfiguration,
        IReadOnlyDictionary<string, ProviderEntry> candidateProviders)
    {
        // Load the captured raw generation. Normalized values must not pass through the secret converter twice.
        using var saved = new ConfigurationManager();
        saved.AddInMemoryCollection(_generationConfiguration);
        var generationProviders = ProviderConfigurationLoader.Load(saved.GetSection("Providers"));
        var generation = PrepareComparisonValues(_generationConfiguration);
        var candidate = PrepareComparisonValues(candidateConfiguration.AsEnumerable());

        foreach (var (name, provider) in generationProviders)
        {
            if (!UsesOAuth(provider)
                || !candidateProviders.TryGetValue(name, out var candidateProvider)
                || !UsesOAuth(candidateProvider))
                continue;

            // The OAuth refresh service owns these exact leaves. Metadata and nested keys remain visible.
            var providerPath = ConfigurationPath.Combine("Providers", name);
            foreach (var field in new[]
                     {
                         nameof(ProviderEntry.OAuthAccessToken), nameof(ProviderEntry.OAuthRefreshToken),
                         nameof(ProviderEntry.OAuthAccountId), nameof(ProviderEntry.OAuthTokenExpiry)
                     })
            {
                var key = ConfigurationPath.Combine(providerPath, field);
                generation.Remove(key);
                candidate.Remove(key);
            }

            // PersistTokens removes the legacy ApiKey. Additions and value changes still require a restart.
            var apiKey = ConfigurationPath.Combine(providerPath, nameof(ProviderEntry.ApiKey));
            if (!candidate.ContainsKey(apiKey))
                generation.Remove(apiKey);
        }

        return (generation, candidate);
    }

    private bool UsesOAuth(ProviderEntry provider)
    {
        // ProviderPluginFactory uses the last plugin for a type key, without case.
        var auth = _plugins.LastOrDefault(plugin =>
            string.Equals(plugin.TypeKey, provider.Type, StringComparison.OrdinalIgnoreCase))?.Auth;
        return auth is OAuthAuth
            || auth is MultiAuth multi
            && provider.AuthMethod is AuthMethod.OAuthDevice or AuthMethod.OAuthPkce
            && multi.SupportedAuthMethods.Contains(provider.AuthMethod);
    }

    private Dictionary<string, string?> PrepareComparisonValues(IEnumerable<KeyValuePair<string, string?>> configuration)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in configuration)
        {
            values.Add(key, value is not null && ISecretsProtector.IsEncrypted(value) && IsRuntimeDecryptedValue(key)
                ? _protector.Unprotect(value)
                : value);
        }
        return values;
    }

    private static bool IsRuntimeDecryptedValue(string key)
    {
        var path = key.Split(ConfigurationPath.KeyDelimiter);
        static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        // BootstrapDeviceSeeder decrypts this root value before it checks the durable device token.
        if (path.Length == 1)
            return Is(path[0], "DeviceToken");

        // Program and RemoteChatChannelBuilder bind these SensitiveString properties.
        if (path.Length == 2)
        {
            if (Is(path[0], "Search"))
            {
                var searchKey = ConfigValueMetadataProvider.Get<SearchConfig>(nameof(SearchConfig.BraveApiKey)).Key;
                return Is(key, searchKey.Replace('.', ':'));
            }
            return Is(path[0], "Slack")
                   && (Is(path[1], nameof(SlackChannelOptions.BotToken)) || Is(path[1], nameof(SlackChannelOptions.AppToken)))
                || Is(path[0], "Discord") && Is(path[1], nameof(DiscordChannelOptions.BotToken))
                || Is(path[0], "Mattermost") && Is(path[1], nameof(MattermostChannelOptions.BotToken));
        }

        if (path.Length == 3)
        {
            if (Is(path[0], "Providers"))
                return Is(path[2], nameof(ProviderEntry.ApiKey))
                    || Is(path[2], nameof(ProviderEntry.OAuthAccessToken))
                    || Is(path[2], nameof(ProviderEntry.OAuthRefreshToken))
                    || Is(path[2], nameof(ProviderEntry.OAuthAccountId));

            if (Is(path[0], "McpServers"))
                return Is(path[2], nameof(McpServerEntry.OAuthClientSecret));

            // McpOAuthCredentialStore decrypts the document before it deserializes these declared fields.
            // Retain their values: resource and authority changes must still cause a restart.
            if (Is(path[0], McpOAuthTokenSet.SecretsSectionKey))
                return Is(path[2], nameof(McpOAuthTokenSet.AccessToken))
                    || Is(path[2], nameof(McpOAuthTokenSet.RefreshToken))
                    || Is(path[2], nameof(McpOAuthTokenSet.ExpiresAt))
                    || Is(path[2], nameof(McpOAuthTokenSet.TokenType))
                    || Is(path[2], nameof(McpOAuthTokenSet.Scope))
                    || Is(path[2], nameof(McpOAuthTokenSet.ObtainedAt))
                    || Is(path[2], nameof(McpOAuthTokenSet.ClientId))
                    || Is(path[2], nameof(McpOAuthTokenSet.ClientSecret))
                    || Is(path[2], nameof(McpOAuthTokenSet.DynamicClientRegistration))
                    || Is(path[2], nameof(McpOAuthTokenSet.AuthorizationServer))
                    || Is(path[2], nameof(McpOAuthTokenSet.TokenEndpointAuthMethod))
                    || Is(path[2], nameof(McpOAuthTokenSet.McpServerUrl))
                    || Is(path[2], nameof(McpOAuthTokenSet.ResourceIdentity));
        }

        if (path.Length == 4)
            return Is(path[0], "SkillFeeds") && Is(path[1], nameof(SkillFeedsConfig.Feeds))
                   && Is(path[3], nameof(SkillFeedSource.ApiKey))
                || Is(path[0], "McpServers")
                   && (Is(path[2], nameof(McpServerEntry.Headers)) || Is(path[2], nameof(McpServerEntry.EnvironmentVariables)));

        return false;
    }

    internal static bool HasConfigurationChanged(
        IReadOnlyDictionary<string, string?> generationConfiguration,
        IEnumerable<KeyValuePair<string, string?>> candidateConfiguration)
    {
        var generation = generationConfiguration.ToDictionary(
            entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var candidate = candidateConfiguration.ToDictionary(
            entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        return candidate.Count != generation.Count
            || generation.Any(entry =>
            {
                var hasKey = candidate.TryGetValue(entry.Key, out var value);
                // config-startup-reconciliation:begin
                return !hasKey || !string.Equals(entry.Value, value, StringComparison.Ordinal);
                // config-startup-reconciliation:end
            });
    }

    private void Reject(string reason)
    {
        _rejectedConfig.Reject(reason);

        // An unchanged invalid file raises many file events (an editor save, a touch). Warn once
        // per reason and file content.
        var rejection = (reason, ContentFingerprint());
        if (rejection == _lastRejection)
        {
            _logger.LogDebug("Config is still rejected: {Reason}", reason);
            return;
        }

        _lastRejection = rejection;
        _logger.LogWarning(
            "Config validation failed. {Reason} Keeping the running config: no change from netclaw.json is applied until it is fixed.",
            reason);
    }

    private string ContentFingerprint()
    {
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_paths.NetclawConfigPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static bool ValidateConfigJson(string path, out string error)
    {
        error = string.Empty;
        if (!File.Exists(path))
            return true; // Missing files are OK — they're optional in the config chain

        try
        {
            var bytes = File.ReadAllBytes(path);
            var reader = new Utf8JsonReader(bytes);
            while (reader.Read()) { } // Walk the entire document to catch syntax errors
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool IsWatchedFile(string? fileName) =>
        fileName is "netclaw.json";

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
        _applicationStartedRegistration.Dispose();
    }
}
