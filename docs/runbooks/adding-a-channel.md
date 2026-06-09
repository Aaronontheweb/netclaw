# Adding a New Channel

This runbook walks through every component needed to add a new chat channel
integration to Netclaw (e.g., Microsoft Teams, WhatsApp, Signal). Each
existing remote chat channel — Slack, Discord, Mattermost — follows this
exact pattern.

**Recommended starting point:** clone an existing channel project (Discord is
the cleanest reference) and rename/adapt it.

## Architecture overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         CHANNEL INTEGRATION LAYERS                          │
│                                                                             │
│  ① Config & Options          ② Transport layer (gateway + reply + outbound) │
│  ③ IChannel service          ④ Actor hierarchy (gateway → conversation)     │
│  ⑤ Channel descriptor        ⑥ Address resolver                            │
│  ⑦ Output renderer           ⑧ LLM tools (send + lookup)                   │
│  ⑨ Reminder target resolver  ⑩ DI registration extension                   │
│  ⑪ Config schema             ⑫ Program.cs wiring                           │
└─────────────────────────────────────────────────────────────────────────────┘

                     ┌──────────────────────────────┐
                     │       ChannelRegistry         │
                     │  (immutable, built at startup) │
                     │                                │
                     │  Descriptors ───── per key     │
                     │  AddressResolvers ─ per key    │
                     │  OutputRenderers ── per key    │
                     │  SnapshotProviders─ per key    │
                     └──────────────────────────────┘
                        ▲           ▲           ▲
           registered   │           │           │   queried by
           at startup   │           │           │   tools, renderers,
                        │           │           │   reminder actor
              ┌─────────┘     ┌─────┘     ┌─────┘
              │               │           │
  ┌───────────────┐  ┌──────────────┐  ┌─────────────┐
  │ Your channel  │  │ Your address │  │ Your output  │
  │ registration  │  │ resolver(s)  │  │ renderer     │
  │ extension     │  │              │  │              │
  └───────┬───────┘  └──────────────┘  └─────────────┘
          │
          │ wires into DI
          ▼
  ┌────────────────────────────────────────────────────┐
  │                   Your IChannel                     │
  │  StartAsync → connect transport, spawn gateway actor│
  │  StopAsync  → drain actors, disconnect transport    │
  │  GetHealthAsync → transport readiness               │
  └──────────┬─────────────────────────────────────────┘
             │ spawns
             ▼
  ┌────────────────────────────────────────────────────┐
  │  XxxGatewayActor → XxxConversationActor (children) │
  │  Routes inbound messages to per-thread sessions     │
  └────────────────────────────────────────────────────┘
```

## Step-by-step

### 1. Add `ChannelType` enum value

**File:** `src/Netclaw.Actors/Channels/ChannelType.cs`

Add your channel to the `ChannelType` enum and update both wire-value methods:

```csharp
// In the enum:
Xxx,

// In ToWireValue():
ChannelType.Xxx => "xxx",

// In TryFromWireValue():
"xxx" => { value = ChannelType.Xxx; return true; }

// In SupportsInteractiveApproval() if applicable:
ChannelType.Xxx => true,
```

### 2. Create the channel project

Create `src/Netclaw.Channels.Xxx/` as a new class library project. Reference
`Netclaw.Channels` and `Netclaw.Actors`.

### 3. Define channel options

**File:** `src/Netclaw.Channels.Xxx/XxxChannelOptions.cs`

```csharp
public sealed class XxxChannelOptions
{
    public bool Enabled { get; set; }
    public SensitiveString BotToken { get; set; } = SensitiveString.Empty;
    public bool AllowDirectMessages { get; set; }
    public HashSet<string> AllowedChannelIds { get; set; } = [];
    public HashSet<string> AllowedUserIds { get; set; } = [];
    // ... channel-specific options
}
```

### 4. Implement transport interfaces

These are the thin abstractions around the external SDK/API. Define them in
your channel project.

#### Gateway transport

Normalizes the SDK's events into Netclaw message types:

```csharp
public interface IXxxGatewayTransport
{
    event Func<XxxGatewayMessage, Task> MessageReceived;
    event Func<Task> Connected;
    event Func<XxxDisconnect, Task> Disconnected;

    bool IsConnected { get; }
    Task<XxxBotIdentity> StartAsync(string token, CancellationToken ct = default);
    Task StopAsync();
}
```

#### Reply client

Sends replies back through the channel in the context of an existing thread:

```csharp
public interface IXxxReplyClient
{
    Task PostReplyAsync(string channelId, string threadId, string text, CancellationToken ct = default);
    // File attachment, reactions, etc.
}
```

#### Outbound client

Proactive posting (new threads, DMs) — used by LLM tools:

```csharp
public interface IXxxOutboundClient
{
    Task<string> PostNewThreadAsync(string channelId, string text, CancellationToken ct = default);
    Task<string> OpenDirectMessageAsync(string userId, string text, CancellationToken ct = default);
}
```

Implement all three against your platform's SDK.

### 5. Implement `IChannel`

**File:** `src/Netclaw.Channels.Xxx/XxxChannel.cs`

```csharp
public sealed class XxxChannel : IChannel
{
    private volatile IActorRef? _gateway;
    private volatile string? _connectFailureDetail;

    // Constructor injection: ActorSystem, ISessionPipeline, SessionIngressGate,
    // transport clients, IChannelRegistry, IContentScanner,
    // IPromptInjectionDetector, IHttpClientFactory, IThreadHistoryFetcher?,
    // IOperationalNotificationSink, TimeProvider, XxxChannelOptions, ILogger,
    // ToolConfig, ModelCapabilities, NetclawPaths

    public ChannelType ChannelType => ChannelType.Xxx;
    public string DisplayName => "Xxx";

    public async Task StartAsync(CancellationToken ct)
    {
        // 1. Check Enabled and BotToken (don't throw — degrade)
        // 2. Connect transport
        // 3. Spawn gateway actor
        // 4. Register in ActorRegistry<XxxGatewayActorKey>
        // 5. Subscribe to transport events
    }

    public async Task StopAsync(CancellationToken ct)
    {
        // 1. Unsubscribe from transport events
        // 2. GracefulStop the gateway actor
        // 3. Disconnect transport
    }

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken ct)
    {
        // Query transport readiness → Healthy / Degraded / Disconnected
    }
}
```

Key patterns from existing channels:
- `_gateway` must be `volatile` — it's read from event handler threads
- `StartAsync` must never throw — a misconfigured channel degrades, it does
  not crash the host
- Connection failures emit an `OperationalAlert` via `IOperationalNotificationSink`

### 6. Implement the actor hierarchy

#### Gateway actor

**File:** `src/Netclaw.Channels.Xxx/XxxGatewayActor.cs`

Top-level actor that receives normalized inbound messages from the transport
and routes them to per-channel/per-guild conversation actor children:

```csharp
internal sealed class XxxGatewayActor : ReceiveActor
{
    // Receive<XxxGatewayMessage>: dedup by event ID, ACL-check, route to child
    // Child: XxxConversationActor (one per channel/guild)
}
```

#### Conversation actor

Routes messages from a specific channel to per-thread session binding actors.
Enforces channel-level ACL and handles passivation.

#### Lifecycle actor (optional but recommended)

If the platform has a persistent WebSocket connection, implement a lifecycle
actor with a reconnection state machine:

```
Disconnected → Starting → Connected → Ready
     ↑                                   │
     └── CleanReconnect ←── on error ────┘
```

See `DiscordNetGatewayLifecycleActor` or `MattermostNetGatewayLifecycleActor`
for the full pattern. Key requirements:
- Exponential backoff on retries via `ScheduleTellOnceCancelable`
- Cancel retry timer in `PostStop()`
- Publish `CleanReconnectRequired` and `ConnectionRestored` events

#### Actor registry key

**File:** `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs`

```csharp
public sealed class XxxGatewayActorKey;
```

Used by `ReminderExecutionActor` to route Mode B reminders to the correct
gateway.

### 7. Implement `IChannelAddressResolver`

**File:** `src/Netclaw.Channels.Xxx/XxxAddressResolver.cs`

```csharp
public sealed class XxxAddressResolver : IChannelAddressResolver
{
    public ChannelDescriptorKey Key { get; }
        = ChannelDescriptorKey.FromChannelType(ChannelType.Xxx);

    public IReadOnlySet<ChannelAddressKind> AddressKinds { get; }
        = new HashSet<ChannelAddressKind> { ChannelAddressKind.Destination, ChannelAddressKind.User };

    public ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken ct = default)
    {
        // Resolve human-friendly targets → canonical IDs:
        //   "#channel-name" → channel ID (Resolved)
        //   "@username" → user ID (Resolved)
        //   raw platform ID → validated (Resolved)
        //   ambiguous query → multiple candidates (Ambiguous)
        //   not found → (NotFound)
        //   ACL-blocked → (NotAllowed)
    }
}
```

Multiple resolvers per channel are supported (e.g., Slack registers both
`SlackTargetResolver` for destinations and `LookupSlackUserTool` for users).

### 8. Implement `IChannelOutputRenderer`

**File:** `src/Netclaw.Channels.Xxx/XxxOutputRenderer.cs`

```csharp
public sealed class XxxOutputRenderer : IChannelOutputRenderer
{
    public ChannelDescriptorKey Key { get; }
        = ChannelDescriptorKey.FromChannelType(ChannelType.Xxx);

    public ValueTask RenderAsync(
        ChannelOutputRenderRequest request,
        CancellationToken ct = default)
    {
        // Handle request.EffectKind:
        //   TextMessage → post text via reply client
        //   ProcessingIndicator → show typing indicator
        //   FileAttachment → upload file
        //   Reaction → add emoji reaction
    }
}
```

If your channel only supports basic text, you can skip effects you don't
support — the registry checks `SupportedOutputEffects` before dispatching.

### 9. Implement `IThreadHistoryFetcher`

**File:** `src/Netclaw.Channels.Xxx/XxxThreadHistoryFetcher.cs`

```csharp
public sealed class XxxThreadHistoryFetcher : IThreadHistoryFetcher
{
    public Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId, CancellationToken ct = default)
    {
        // Fetch all prior messages in the thread identified by sessionId
        // Return in chronological order
        // Return empty list on failure (don't throw)
    }
}
```

### 10. Implement LLM tools

#### Send message tool

**File:** `src/Netclaw.Channels.Xxx/Tools/SendXxxMessageTool.cs`

```csharp
public sealed partial class SendXxxMessageTool : NetclawTool<SendXxxMessageTool.Params>, IChannelTool
{
    // LLM-facing name: "send_xxx_message"
    // Uses IXxxOutboundClient to post messages
    // ACL-checks against XxxChannelOptions.AllowedChannelIds
}
```

#### User lookup tool (optional)

**File:** `src/Netclaw.Channels.Xxx/Tools/LookupXxxUserTool.cs`

Only needed if the platform has a user directory API.

### 11. Implement `IReminderTargetResolver`

**File:** `src/Netclaw.Channels.Xxx/XxxReminderTargetResolver.cs`

```csharp
public sealed class XxxReminderTargetResolver : IReminderTargetResolver
{
    public string Transport => "xxx";

    public Task<ReminderTargetResolution> ResolveAsync(string target, CancellationToken ct = default)
    {
        // Resolve "@user" → (Success, userId, User)
        // Resolve "#channel" → (Success, channelId, Channel)
        // Resolve unknown → (false, null, Unknown, errorMessage)
    }
}
```

### 12. Write the DI registration extension

**File:** `src/Netclaw.Daemon/Configuration/XxxChannelRegistrationExtensions.cs`

This is the single entry point that wires everything into DI:

```csharp
public static class XxxChannelRegistrationExtensions
{
    public static void AddXxxChannelIntegration(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Xxx").Get<XxxChannelOptions>()
            ?? new XxxChannelOptions();
        services.AddSingleton(options);

        // Always register the descriptor (even when disabled — the registry
        // needs it for ListChannels)
        services.AddChannelRegistry();
        services.AddChannelDescriptorWithRuntimeSnapshot(CreateDescriptor(options));

        if (!options.Enabled)
            return;

        // Transport clients
        services.AddHttpClient("xxx-files").AddNetclawHeaders("xxx-files");
        services.AddSingleton<IXxxGatewayTransport, XxxNetGatewayTransport>();
        services.AddSingleton<IXxxReplyClient, XxxReplyClient>();
        services.AddSingleton<IXxxOutboundClient, XxxOutboundClient>();

        // Thread history
        services.AddSingleton<IThreadHistoryFetcher, XxxThreadHistoryFetcher>();

        // Address resolution
        services.AddSingleton<IChannelAddressResolver, XxxAddressResolver>();

        // Output rendering
        services.AddSingleton<IChannelOutputRenderer, XxxOutputRenderer>();

        // Reminder target resolution
        services.AddSingleton<IReminderTargetResolver, XxxReminderTargetResolver>();

        // Channel service (keyed so multiple IChannel impls coexist)
        services.AddKeyedSingleton<IChannel, XxxChannel>("xxx");
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>("xxx"));
        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>("xxx"));

        // LLM tools
        services.AddSingleton<SendXxxMessageTool>();
        services.AddSingleton<IChannelTool>(sp =>
            sp.GetRequiredService<SendXxxMessageTool>());
    }

    private static ChannelDescriptor CreateDescriptor(XxxChannelOptions options)
        => ChannelDescriptor.CreateRemoteChat(
            ChannelType.Xxx,
            "Xxx",
            options.Enabled,
            options.AllowDirectMessages,
            // Pass additional output effects beyond the TextMessage/FileAttachment baseline:
            new HashSet<ChannelOutputEffectKind> { ChannelOutputEffectKind.ProcessingIndicator });
}
```

### 13. Update the config schema

**File:** `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`

Add a new top-level `"Xxx"` section with properties matching your
`XxxChannelOptions` class. The schema uses `"additionalProperties": false`
throughout — any property missing from the schema will be rejected by
`ConfigSchemaDoctorCheck` at runtime.

Follow the migration-friendly rules in `CLAUDE.md` § Configuration Schema
Sync Rule:
- New required properties need a `"default"` value
- Enum properties use `"type": "string"` with named values
- Removals are handled automatically

### 14. Wire into Program.cs

**File:** `src/Netclaw.Daemon/Program.cs` (around line 1074)

```csharp
services.AddXxxChannelIntegration(configuration);
```

Add it alongside the existing channel registrations. No other changes needed —
`AddChannelSendTools`, `AddChannelLookupTools`, and
`ChannelToolRegistration.RegisterChannelTools` all auto-discover via DI.

### 15. Update generic tool dispatchers

Two files need your channel added to their enabled-channel checks:

**`src/Netclaw.Daemon/Configuration/ChannelSendTools.cs`** — update
`AddChannelSendTools` to include your channel in the
`slackEnabled || discordEnabled || ...` check.

**`src/Netclaw.Daemon/Configuration/ChannelLookupTools.cs`** — update
`AddChannelLookupTools` similarly.

## Testing checklist

### Unit tests

Create `src/Netclaw.Actors.Tests/Channels/Xxx*.cs` tests covering:

- [ ] Gateway actor message routing and deduplication
- [ ] Conversation actor ACL enforcement
- [ ] Lifecycle actor state machine transitions (if applicable)
- [ ] Address resolver: exact match, substring match, ambiguous, not found, ACL-blocked
- [ ] Transport fake that implements `IXxxGatewayTransport` for actor tests
- [ ] Reminder target resolver: user, channel, unknown

### Registration tests

Add cases to `src/Netclaw.Daemon.Tests/Configuration/ChannelRegistryRegistrationTests.cs`:

- [ ] Descriptor appears in `ListChannels()` with correct key, kind, capabilities
- [ ] `SupportedOutputEffects` includes expected effects
- [ ] `ToolIntents` includes `SendMessage`
- [ ] Address resolver is registered and routable
- [ ] Output renderer is registered
- [ ] Disabled channel still has a disabled descriptor
- [ ] LLM tools are discoverable via `IChannelTool`

### Integration / smoke tests

- [ ] Channel connects and receives a test message end-to-end
- [ ] Channel health reports correctly (healthy, degraded, disconnected)
- [ ] `send_xxx_message` tool posts a message via the LLM
- [ ] Reminder delivery routes to the correct channel
- [ ] `netclaw doctor` validates the new config section

## Files changed summary

| Layer | Files |
|-------|-------|
| Enum | `src/Netclaw.Actors/Channels/ChannelType.cs` |
| Actor key | `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs` |
| Channel project | `src/Netclaw.Channels.Xxx/` (new) |
| Options | `XxxChannelOptions.cs` |
| Transport | `IXxxGatewayTransport.cs`, `XxxNetGatewayTransport.cs` |
| Reply/outbound | `IXxxReplyClient.cs`, `IXxxOutboundClient.cs`, impls |
| IChannel | `XxxChannel.cs` |
| Gateway actor | `XxxGatewayActor.cs`, `XxxConversationActor.cs` |
| Lifecycle actor | `XxxNetGatewayLifecycleActor.cs` (if WebSocket) |
| Address resolver | `XxxAddressResolver.cs` |
| Output renderer | `XxxOutputRenderer.cs` |
| Thread history | `XxxThreadHistoryFetcher.cs` |
| LLM tools | `Tools/SendXxxMessageTool.cs`, `Tools/LookupXxxUserTool.cs` |
| Reminder resolver | `XxxReminderTargetResolver.cs` |
| DI registration | `src/Netclaw.Daemon/Configuration/XxxChannelRegistrationExtensions.cs` |
| Generic tools | `ChannelSendTools.cs`, `ChannelLookupTools.cs` (update checks) |
| Config schema | `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` |
| Wiring | `src/Netclaw.Daemon/Program.cs` |
| Tests | `src/Netclaw.Actors.Tests/Channels/Xxx*.cs` |
| Registration tests | `src/Netclaw.Daemon.Tests/Configuration/ChannelRegistryRegistrationTests.cs` |
