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

// Load local overrides (appsettings.Local.json is gitignored for machine-specific config)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

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
        .WithInMemoryJournal()
        .WithInMemorySnapshotStore()
        .WithNetclawActors();
});

// -- Console adapter (TUI proof-of-concept) --
builder.Services.AddHostedService<ConsoleAdapter>();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Netclaw");
logger.LogInformation("Netclaw starting (model={Model}, endpoint={Endpoint})", ollamaModel, ollamaUrl);

await app.RunAsync();
