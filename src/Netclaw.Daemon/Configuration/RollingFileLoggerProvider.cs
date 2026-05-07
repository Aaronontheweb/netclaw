// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Globalization;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Simple file-based logger that writes to a daily rolling log file.
/// Uses a background queue to avoid blocking callers.
///
/// Session-scoped lines (emitted under a populated
/// <see cref="SessionDiagnosticsContext"/>) are mirrored to a per-session
/// <c>session.log</c> by routing through the <c>SessionLogDispatcher</c>
/// actor. The dispatcher serializes all writes for a given session through
/// a single mailbox, replacing the in-process file lock that previously
/// coordinated concurrent writers.
/// </summary>
internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB per file
    private const int PreResolutionBufferLimit = 1000;

    private readonly string _basePath;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly BlockingCollection<string> _queue = new(1024);
    private readonly Thread _writerThread;
    private readonly ConcurrentQueue<SessionLogDiagnostic>? _pendingDiagnostics;
    private IActorRef? _sessionDispatcher;
    private StreamWriter? _writer;
    private string _currentDate = "";

    public RollingFileLoggerProvider(string basePath, Func<Task<IActorRef>>? sessionDispatcherFactory = null, TimeProvider? timeProvider = null)
    {
        _basePath = basePath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _writerThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "NetclawLogWriter"
        };
        _writerThread.Start();

        if (sessionDispatcherFactory is not null)
        {
            _pendingDiagnostics = new ConcurrentQueue<SessionLogDiagnostic>();
            _ = ResolveSessionDispatcherAsync(sessionDispatcherFactory);
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));

    internal void Enqueue(string message)
    {
        _queue.TryAdd(message);

        if (_pendingDiagnostics is null)
            return;

        var sessionId = SessionDiagnosticsContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var diagnostic = new SessionLogDiagnostic
        {
            SessionId = new SessionId(sessionId),
            Line = $"[{_timeProvider.GetUtcNow():o}] Diagnostic: {message}"
        };

        var dispatcher = Volatile.Read(ref _sessionDispatcher);
        if (dispatcher is not null)
        {
            dispatcher.Tell(diagnostic);
            return;
        }

        if (_pendingDiagnostics.Count >= PreResolutionBufferLimit)
            return;
        _pendingDiagnostics.Enqueue(diagnostic);
    }

    private async Task ResolveSessionDispatcherAsync(Func<Task<IActorRef>> factory)
    {
        try
        {
            var dispatcher = await factory().ConfigureAwait(false);

            while (_pendingDiagnostics!.TryDequeue(out var pending))
                dispatcher.Tell(pending);

            Volatile.Write(ref _sessionDispatcher, dispatcher);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NetclawLogWriter] Failed to resolve session log dispatcher: {ex.Message}");
        }
    }

    private void ProcessQueue()
    {
        foreach (var message in _queue.GetConsumingEnumerable())
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(message);
                _writer.Flush();
            }
            catch (Exception ex)
            {
                // Last-resort: write to stderr to avoid silent swallow
                Console.Error.WriteLine($"[NetclawLogWriter] Failed to write log: {ex.Message}");
            }
        }
    }

    private void EnsureWriter()
    {
        var today = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_writer is not null && _currentDate == today)
        {
            // Roll if file exceeds size limit
            if (_writer.BaseStream.Length >= MaxFileSizeBytes)
            {
                _writer.Dispose();
                _writer = null;
            }
            else
            {
                return;
            }
        }

        _writer?.Dispose();
        _currentDate = today;

        var dir = Path.GetDirectoryName(_basePath)!;
        var name = Path.GetFileNameWithoutExtension(_basePath);
        var ext = Path.GetExtension(_basePath);
        var path = Path.Combine(dir, $"{name}-{today}{ext}");

        _writer = new StreamWriter(path, append: true) { AutoFlush = false };
    }

    internal string GetTimestamp()
    {
        return _timeProvider.GetUtcNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _writer?.Dispose();
    }
}

internal sealed class RollingFileLogger : ILogger
{
    private readonly string _category;
    private readonly RollingFileLoggerProvider _provider;

    public RollingFileLogger(string category, RollingFileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var timestamp = _provider.GetTimestamp();
        var level = logLevel switch
        {
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "DBG"
        };

        var message = formatter(state, exception);
        var line = $"{timestamp} [{level}] {_category}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        _provider.Enqueue(line);
    }
}
