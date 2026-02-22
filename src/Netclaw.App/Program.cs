using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Sessions;
using OllamaSharp;

var builder = Host.CreateApplicationBuilder(args);

// -- Netclaw paths (creates ~/.netclaw/ structure) --
var paths = new NetclawPaths();
paths.EnsureDirectoriesExist();
builder.Services.AddSingleton(paths);

// Load local overrides from ~/.netclaw/config/ (machine-specific, not in source control)
var localConfigPath = Path.Combine(paths.ConfigDirectory, "appsettings.Local.json");
builder.Configuration.AddJsonFile(localConfigPath, optional: true, reloadOnChange: false);

// Suppress all framework console logging — session logs go to disk,
// console is reserved for the chat UI
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// -- Ollama IChatClient --
var ollamaUrl = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
var ollamaModel = builder.Configuration["Ollama:Model"] ?? "qwen3:30b";

builder.Services.AddSingleton<IChatClient>(
    new OllamaApiClient(new Uri(ollamaUrl), ollamaModel));

// -- Session configuration --
builder.Services.AddSingleton(new SessionConfig
{
    ModelId = ollamaModel,
    ContextWindowTokens = 32_768 // qwen3:30b default
});

// -- System prompt --
builder.Services.AddSingleton<ISystemPromptProvider>(
    new StaticSystemPromptProvider(
        "You are Netclaw, a helpful homelab operations assistant. Be concise and direct."));

// -- Akka.NET actor system --
builder.Services.AddAkka("netclaw", (akkaBuilder, sp) =>
{
    akkaBuilder
        .ConfigureLoggers(setup =>
        {
            setup.ClearLoggers();
            setup.AddLoggerFactory();
            setup.LogLevel = Akka.Event.LogLevel.WarningLevel;
        })
        .WithInMemoryJournal()
        .WithInMemorySnapshotStore()
        .WithNetclawActors();
});

// -- Console adapter (TUI proof-of-concept) --
builder.Services.AddHostedService<ConsoleAdapter>();

await builder.Build().RunAsync();
