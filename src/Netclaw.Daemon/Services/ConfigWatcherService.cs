// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Processes <c>netclaw.json</c> events from the host generation's file watcher.
/// Debounces events and validates the canonical sources before a coordinated restart.
/// See SPEC-011 §Configuration Restart Coordination.
/// </summary>
public sealed class ConfigWatcherService : IHostedService, IDisposable
{
    private readonly NetclawPaths _paths;
    private readonly ConfigFileWatch _watch;
    private readonly TimeProvider _timeProvider;
    private readonly IDaemonRestartCoordinator _restartCoordinator;
    private readonly RejectedConfigState _rejectedConfig;
    private readonly IReadOnlyList<ILlmProviderPlugin> _plugins;
    private readonly IModelCapabilityResolver _capabilities;
    private readonly ILoggerFactory _loggerFactory;
    private (string Reason, string Content)? _lastRejection;
    private readonly ILogger<ConfigWatcherService> _logger;
    private readonly IReadOnlyDictionary<string, string?> _lowerPriorityConfiguration;
    private readonly TaskCompletionSource _applicationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _applicationStartedRegistration;

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

    internal ConfigWatcherService(
        NetclawPaths paths,
        ConfigFileWatch watch,
        TimeProvider timeProvider,
        IDaemonRestartCoordinator restartCoordinator,
        RejectedConfigState rejectedConfig,
        IEnumerable<ILlmProviderPlugin> plugins,
        IModelCapabilityResolver capabilities,
        ILoggerFactory loggerFactory,
        ILogger<ConfigWatcherService> logger,
        IHostApplicationLifetime applicationLifetime,
        IReadOnlyDictionary<string, string?> lowerPriorityConfiguration)
    {
        _capabilities = capabilities;
        _loggerFactory = loggerFactory;
        _rejectedConfig = rejectedConfig;
        _plugins = plugins.ToList();
        _paths = paths;
        _watch = watch;
        _timeProvider = timeProvider;
        _restartCoordinator = restartCoordinator;
        _logger = logger;
        _lowerPriorityConfiguration = lowerPriorityConfiguration;
        _applicationStartedRegistration = applicationLifetime.ApplicationStarted.Register(
            () => _applicationStarted.TrySetResult());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _watch.Attach(ScheduleReload, _logger);
        _logger.LogInformation("Config hot-reload watching: {ConfigDir}", _paths.ConfigDirectory);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watch.Detach();
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
        }
        _applicationStartedRegistration.Dispose();
        return Task.CompletedTask;
    }

    private void ScheduleReload()
    {
        // The owner lock precedes this lock; stop detaches before it cancels this task.
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            _pendingReload = RunDebouncedReloadAsync(_debounceCts.Token);
        }
    }

    private async Task RunDebouncedReloadAsync(CancellationToken token)
    {
        try
        {
            // Debounce on the injected TimeProvider so tests can virtualize it.
            await Task.Delay(_debounceInterval, _timeProvider, token);
            // A restart must not stop the host before its other services start.
            await _applicationStarted.Task.WaitAsync(token);
            if (token.IsCancellationRequested)
                return;

            await ApplyReloadAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Config reload debounce cancelled by newer event");
        }
    }

    internal async Task ApplyReloadAsync(CancellationToken cancellationToken)
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
            try
            {
                reason = FindRejection(candidate).Error;
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
            // Match the JSON syntax accepted by AddNetclawDaemonSources.
            var options = new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var reader = new Utf8JsonReader(bytes, options);
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
        _watch.Detach();
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
        _applicationStartedRegistration.Dispose();
    }
}

internal sealed class ConfigFileWatch : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly object _ownerLock = new();
    private Action? _reload;
    private ILogger<ConfigWatcherService>? _logger;
    private bool _pendingReload;
    private bool _detached;

    internal ConfigFileWatch(NetclawPaths paths)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        _watcher = new FileSystemWatcher(paths.ConfigDirectory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = false
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;
    }

    internal void Arm()
    {
        lock (_ownerLock)
        {
            if (_detached)
                throw new InvalidOperationException("The config file watch is detached.");

            _watcher.EnableRaisingEvents = true;
        }
    }

    internal void Attach(Action reload, ILogger<ConfigWatcherService> logger)
    {
        ArgumentNullException.ThrowIfNull(reload);
        ArgumentNullException.ThrowIfNull(logger);
        lock (_ownerLock)
        {
            if (_detached || _reload is not null)
                throw new InvalidOperationException("The config file watch cannot attach another consumer.");

            _reload = reload;
            _logger = logger;
            if (_pendingReload)
            {
                _pendingReload = false;
                _reload();
            }
        }
    }

    internal void Detach()
    {
        lock (_ownerLock)
        {
            _detached = true;
            _pendingReload = false;
            _reload = null;
            _logger = null;
        }
    }

    internal void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_ownerLock)
        {
            if (_detached || !ConfigWatcherService.IsWatchedFile(e.Name))
                return;

            try
            {
                _logger?.LogDebug("Config file changed: {FileName}", e.Name);
                ReloadOrRetain();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled exception in config watcher Changed callback for {FileName}", e.Name);
            }
        }
    }

    internal void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_ownerLock)
        {
            if (_detached || !ConfigWatcherService.IsWatchedFile(e.Name))
                return;

            try
            {
                _logger?.LogWarning("Config file deleted: {FileName}. Keeping current config.", e.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled exception in config watcher Deleted callback for {FileName}", e.Name);
            }
        }
    }

    internal void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        lock (_ownerLock)
        {
            if (_detached)
                return;

            try
            {
                if (ConfigWatcherService.IsWatchedFile(e.Name))
                {
                    _logger?.LogDebug("Config file rename detected: {OldName} -> {NewName}", e.OldName, e.Name);
                    ReloadOrRetain();
                }
                else if (ConfigWatcherService.IsWatchedFile(e.OldName))
                {
                    _logger?.LogWarning("Config file renamed away: {OldName} -> {NewName}. Keeping current config.", e.OldName, e.Name);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled exception in config watcher Renamed callback for {FileName}", e.Name);
            }
        }
    }

    private void ReloadOrRetain()
    {
        // The owner lock precedes the service debounce lock during callback delivery.
        if (_reload is not null)
        {
            _reload();
        }
        else
        {
            // config-startup-reconciliation:begin
            _pendingReload = true;
            // config-startup-reconciliation:end
        }
    }

    public void Dispose()
    {
        Detach();
        // Native disposal can wait for a callback that needs the owner lock.
        _watcher.Dispose();
    }
}
