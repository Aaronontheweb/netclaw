// -----------------------------------------------------------------------
// <copyright file="ProbeProgressReporter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Model;

/// <summary>
/// Emits a live "probing … (Ns)" elapsed-time ticker while a provider probe is in
/// flight, so a slow self-hosted server reads as "working, just slow" instead of a
/// hang (#1292). The ticker goes to <b>stderr</b> and is suppressed entirely when
/// stderr is redirected, so piped stdout (the discovered-model table) stays clean and
/// machine-parseable. Start it, run the probe inside the <c>await using</c> scope, and
/// disposal stops the ticker and erases its line.
/// </summary>
internal sealed class ProbeProgressReporter : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly TextWriter _err;

    private ProbeProgressReporter(string endpoint, TextWriter err, TimeProvider time)
    {
        _err = err;
        _loop = RunAsync(endpoint, time, _cts.Token);
    }

    internal static ProbeProgressReporter Start(
        string endpoint, TextWriter? err = null, TimeProvider? time = null)
        => new(endpoint, err ?? System.Console.Error, time ?? TimeProvider.System);

    private async Task RunAsync(string endpoint, TimeProvider time, CancellationToken ct)
    {
        // Redirected stderr (pipes, CI, test harnesses) must not receive carriage-return
        // animation — it would corrupt captured logs. The probe still runs; it just
        // tracks elapsed time silently.
        if (System.Console.IsErrorRedirected)
            return;

        var start = time.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(TickInterval, time, ct);
                var seconds = (int)time.GetElapsedTime(start).TotalSeconds;
                await _err.WriteAsync($"\r  probing {endpoint} ... {seconds}s ");
                await _err.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            return; // probe completed (or was cancelled) — stop ticking
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _loop;

        if (!System.Console.IsErrorRedirected)
        {
            // Erase the ticker line so the result prints on a clean row.
            await _err.WriteAsync("\r" + new string(' ', 44) + "\r");
            await _err.FlushAsync();
        }

        _cts.Dispose();
    }
}
