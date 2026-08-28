// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Cli.Daemon;
using Netclaw.Security;
using Netclaw.Tools;
using R3;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Evals.InteractiveClient;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Netclaw.Evals.InteractiveClient <daemon-endpoint> <prompt>");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await RunAsync(args[0], args[1], cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Interactive eval client failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(
        string daemonEndpoint,
        string prompt,
        CancellationToken cancellationToken)
    {
        var outputs = Channel.CreateUnbounded<SessionOutput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var approvalPolicy = new ManagedTemporaryEvalApprovalPolicy();

        await using var client = new DaemonClient(daemonEndpoint);
        using var subscription = client.SessionOutput.Subscribe(output => outputs.Writer.TryWrite(output));

        await client.ConnectAsync(cancellationToken);
        await client.CreateSessionAsync(ChannelType.SignalR, cancellationToken);
        await client.SendAsync(prompt, cancellationToken);

        await foreach (var output in outputs.Reader.ReadAllAsync(cancellationToken))
        {
            var dto = SessionOutputDtoMapper.ToDto(output);
            Console.WriteLine(JsonSerializer.Serialize(dto, JsonOptions));

            approvalPolicy.Observe(output);

            if (output is ToolInteractionRequest interaction)
            {
                var selectedKey = approvalPolicy.SelectResponse(interaction);
                Console.Error.WriteLine(
                    $"Interactive eval client selected '{selectedKey.Value}' for {interaction.ToolName.Value}.");
                await client.RespondToInteractionAsync(
                    interaction.CallId.Value,
                    selectedKey.Value,
                    cancellationToken);
            }

            if (output is TurnCompleted)
                return 0;
        }

        return 1;
    }
}

internal sealed class ManagedTemporaryEvalApprovalPolicy
{
    private const string CorrectionPrefix =
        "Tool execution deferred: use_managed_temporary_directory\nManaged temporary directory: '";

    private readonly Dictionary<ToolCallId, ToolCallOutput> _toolCalls = [];
    private string? _managedTemporaryDirectory;
    private bool _approvedManagedTemporaryWrite;

    internal void Observe(SessionOutput output)
    {
        switch (output)
        {
            case ToolCallOutput call:
                _toolCalls[call.CallId] = call;
                break;
            case ToolResultOutput result when TryReadManagedTemporaryDirectory(result.Result, out var directory):
                _managedTemporaryDirectory = directory;
                break;
        }
    }

    internal ApprovalOptionKey SelectResponse(ToolInteractionRequest interaction)
    {
        if (CanApproveManagedTemporaryWrite(interaction))
        {
            var approveOnce = interaction.Options.FirstOrDefault(option =>
                string.Equals(option.Key.Value, ApprovalOptionKeys.ApproveOnce, StringComparison.Ordinal));
            if (approveOnce is not null)
            {
                _approvedManagedTemporaryWrite = true;
                return approveOnce.Key;
            }
        }

        var deny = interaction.Options.FirstOrDefault(option =>
            string.Equals(option.Key.Value, ApprovalOptionKeys.Deny, StringComparison.Ordinal));
        return deny?.Key
               ?? throw new InvalidOperationException("The approval request has no deny option.");
    }

    private bool CanApproveManagedTemporaryWrite(ToolInteractionRequest interaction)
    {
        if (_approvedManagedTemporaryWrite
            || _managedTemporaryDirectory is null
            || !string.Equals(interaction.ToolName.Value, FileWriteTool.ToolName, StringComparison.Ordinal)
            || !_toolCalls.TryGetValue(interaction.CallId, out var call)
            || string.IsNullOrWhiteSpace(call.ArgumentsJson))
        {
            return false;
        }

        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            if (!arguments.RootElement.TryGetProperty("Path", out var pathElement)
                || pathElement.ValueKind != JsonValueKind.String
                || pathElement.GetString() is not { } path)
            {
                return false;
            }

            return PathUtility.TryNormalize(path, out var normalizedPath)
                   && PathUtility.TryNormalize(_managedTemporaryDirectory, out var normalizedRoot)
                   && PathUtility.IsWithinRoot(normalizedPath, normalizedRoot);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadManagedTemporaryDirectory(string result, out string directory)
    {
        directory = string.Empty;
        if (!result.StartsWith(CorrectionPrefix, StringComparison.Ordinal))
            return false;

        var start = CorrectionPrefix.Length;
        var end = result.IndexOf("'.", start, StringComparison.Ordinal);
        if (end <= start)
            return false;

        var candidate = result[start..end];
        if (!PathUtility.TryNormalize(candidate, out var normalized))
            return false;

        directory = normalized;
        return true;
    }
}
