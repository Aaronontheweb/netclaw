using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;

/// <summary>
/// Hosted service that provides a bare console chat loop for proving
/// the actor system works end-to-end with a real LLM.
///
/// Creates a subscriber actor to receive session outputs and a while loop
/// that reads user input from stdin and sends it to the session actor.
///
/// All session activity is logged to ~/.netclaw/logs/{sessionId}.log.
/// Console output is reserved exclusively for the chat UI.
///
/// This is a temporary proof-of-concept adapter. It will be replaced by
/// a proper CLI framework (Cocona) and TUI (Termina) in later tasks.
/// </summary>
public sealed class ConsoleAdapter : IHostedService
{
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly ActorSystem _actorSystem;
    private readonly NetclawPaths _paths;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConsoleAdapter> _logger;

    private CancellationTokenRegistration _shutdownRegistration;

    public ConsoleAdapter(
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        ActorSystem actorSystem,
        NetclawPaths paths,
        IHostApplicationLifetime lifetime,
        ILogger<ConsoleAdapter> logger)
    {
        _sessionManagerProvider = sessionManagerProvider;
        _actorSystem = actorSystem;
        _paths = paths;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run the chat loop on a background thread so we don't block host startup
        _shutdownRegistration = _lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(() => RunChatLoopAsync(_lifetime.ApplicationStopping), CancellationToken.None);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownRegistration.Dispose();
        return Task.CompletedTask;
    }

    private async Task RunChatLoopAsync(CancellationToken stopping)
    {
        try
        {
            var sessionManager = await _sessionManagerProvider.GetAsync(stopping);
            var sessionId = new SessionId($"tui/{Guid.NewGuid():N}");

            // Set up session log file
            _paths.EnsureDirectoriesExist();
            var logFileName = $"{sessionId.Value.Replace("/", "-")}.log";
            var logPath = Path.Combine(_paths.LogsDirectory, logFileName);
            var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };

            logWriter.WriteLine($"[{DateTimeOffset.UtcNow:o}] Session started: {sessionId}");

            // Create subscriber actor that writes session output to console + log
            var subscriber = _actorSystem.ActorOf(
                Props.Create(() => new ConsoleSubscriberActor(logWriter)),
                $"console-subscriber-{sessionId.Value.Replace("/", "-")}");

            // Join the session
            sessionManager.Tell(new JoinSession
            {
                SessionId = sessionId,
                Subscriber = subscriber,
                Filter = OutputFilter.Full
            });

            _logger.LogInformation("Session started: {SessionId} (log: {LogPath})", sessionId, logPath);
            Console.WriteLine();
            Console.WriteLine($"Netclaw console chat (log: {logPath})");
            Console.WriteLine("Type 'exit' to quit.");
            Console.WriteLine("──────────────────────────────────────────");
            Console.WriteLine();

            while (!stopping.IsCancellationRequested)
            {
                Console.Write("You> ");
                var input = Console.ReadLine();

                if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                {
                    logWriter.WriteLine($"[{DateTimeOffset.UtcNow:o}] User exited chat");
                    _lifetime.StopApplication();
                    break;
                }

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                logWriter.WriteLine($"[{DateTimeOffset.UtcNow:o}] USER: {input}");

                sessionManager.Tell(new SendUserMessage
                {
                    SessionId = sessionId,
                    Content = input
                });
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Console chat loop cancelled (shutdown)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Console chat loop failed");
            _lifetime.StopApplication();
        }
    }
}

/// <summary>
/// Minimal actor that receives session outputs and writes them to the console
/// and a per-session log file. Needed because session outputs are delivered via
/// Akka Tell to an IActorRef subscriber.
/// </summary>
public sealed class ConsoleSubscriberActor : ReceiveActor
{
    private readonly StreamWriter _log;

    public ConsoleSubscriberActor(StreamWriter logWriter)
    {
        _log = logWriter;

        Receive<SessionJoined>(msg =>
        {
            Log($"SESSION_JOINED turn_count={msg.TurnCount} title={msg.Title ?? "(none)"}");
        });

        Receive<TextOutput>(msg =>
        {
            Console.WriteLine();
            Console.WriteLine($"Netclaw> {msg.Text}");
            Console.WriteLine();
            Log($"ASSISTANT: {msg.Text}");
        });

        Receive<ThinkingOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [thinking] {msg.Text}");
            Console.ResetColor();
            Log($"THINKING: {msg.Text}");
        });

        Receive<ToolCallOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  [tool] {msg.ToolName}({msg.ArgumentsJson ?? ""})");
            Console.ResetColor();
            Log($"TOOL_CALL: {msg.ToolName} call_id={msg.CallId} args={msg.ArgumentsJson ?? "{}"}");
        });

        Receive<UsageOutput>(msg =>
        {
            var usage = msg.UsagePercent.HasValue
                ? $" ({msg.UsagePercent.Value:P0} context)"
                : "";
            Log($"USAGE: in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens} cached={msg.CachedInputTokens} reasoning={msg.ReasoningTokens} context_window={msg.ContextWindowTokens}{usage}");
        });

        Receive<ErrorOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [error] {msg.Message}");
            Console.ResetColor();
            Log($"ERROR: {msg.Message}");
        });

        Receive<TurnCompleted>(msg =>
        {
            Log($"TURN_COMPLETED: turn={msg.TurnNumber}");
        });

        Receive<CompactionOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  [compaction] {msg.MessagesBefore} → {msg.MessagesAfter} messages");
            Console.ResetColor();
            Log($"COMPACTION: before={msg.MessagesBefore} after={msg.MessagesAfter} tool_results_cleared={msg.ToolResultsCleared} summarized={msg.Summarized}");
        });
    }

    private void Log(string message)
    {
        _log.WriteLine($"[{DateTimeOffset.UtcNow:o}] {message}");
    }

    protected override void PostStop()
    {
        Log("SESSION_ENDED");
        _log.Dispose();
        base.PostStop();
    }
}
