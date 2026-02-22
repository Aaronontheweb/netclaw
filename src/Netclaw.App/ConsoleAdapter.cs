using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;

/// <summary>
/// Hosted service that provides a bare console chat loop for proving
/// the actor system works end-to-end with a real LLM.
///
/// Creates a subscriber actor to receive session outputs and a while loop
/// that reads user input from stdin and sends it to the session actor.
///
/// This is a temporary proof-of-concept adapter. It will be replaced by
/// a proper CLI framework (Cocona) and TUI (Termina) in later tasks.
/// </summary>
public sealed class ConsoleAdapter : IHostedService
{
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly ActorSystem _actorSystem;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConsoleAdapter> _logger;

    private CancellationTokenRegistration _shutdownRegistration;

    public ConsoleAdapter(
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        ActorSystem actorSystem,
        IHostApplicationLifetime lifetime,
        ILogger<ConsoleAdapter> logger)
    {
        _sessionManagerProvider = sessionManagerProvider;
        _actorSystem = actorSystem;
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

            // Create subscriber actor that writes session output to the console
            var subscriber = _actorSystem.ActorOf(
                Props.Create(() => new ConsoleSubscriberActor()),
                $"console-subscriber-{sessionId.Value.Replace("/", "-")}");

            // Join the session
            sessionManager.Tell(new JoinSession
            {
                SessionId = sessionId,
                Subscriber = subscriber,
                Filter = OutputFilter.Full
            });

            _logger.LogInformation("Session started: {SessionId}", sessionId);
            Console.WriteLine();
            Console.WriteLine("Netclaw console chat (type 'exit' to quit)");
            Console.WriteLine("──────────────────────────────────────────");
            Console.WriteLine();

            while (!stopping.IsCancellationRequested)
            {
                Console.Write("You> ");
                var input = Console.ReadLine();

                if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("User exited chat");
                    _lifetime.StopApplication();
                    break;
                }

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                sessionManager.Tell(new SendUserMessage
                {
                    SessionId = sessionId,
                    Content = input
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Console chat loop failed");
            _lifetime.StopApplication();
        }
    }
}

/// <summary>
/// Minimal actor that receives session outputs and writes them to the console.
/// Needed because session outputs are delivered via Akka Tell to an IActorRef subscriber.
/// </summary>
public sealed class ConsoleSubscriberActor : ReceiveActor
{
    public ConsoleSubscriberActor()
    {
        Receive<SessionJoined>(msg =>
        {
            // Session joined ack — no visible output needed
        });

        Receive<TextOutput>(msg =>
        {
            Console.WriteLine();
            Console.WriteLine($"Netclaw> {msg.Text}");
            Console.WriteLine();
        });

        Receive<ThinkingOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [thinking] {msg.Text}");
            Console.ResetColor();
        });

        Receive<ToolCallOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  [tool] {msg.ToolName}({msg.ArgumentsJson ?? ""})");
            Console.ResetColor();
        });

        Receive<UsageOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var usage = msg.UsagePercent.HasValue
                ? $" ({msg.UsagePercent.Value:P0} context)"
                : "";
            Console.WriteLine($"  [usage] in={msg.InputTokens} out={msg.OutputTokens}{usage}");
            Console.ResetColor();
        });

        Receive<ErrorOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [error] {msg.Message}");
            Console.ResetColor();
        });

        Receive<TurnCompleted>(_ =>
        {
            // Turn boundary — prompt is handled by the main loop
        });

        Receive<CompactionOutput>(msg =>
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  [compaction] {msg.MessagesBefore} → {msg.MessagesAfter} messages");
            Console.ResetColor();
        });
    }
}
