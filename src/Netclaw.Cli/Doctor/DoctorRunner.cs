// -----------------------------------------------------------------------
// <copyright file="DoctorRunner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Doctor;

public sealed class DoctorRunner(IEnumerable<IDoctorCheck> checks)
{
    public async Task<DoctorRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DoctorCheckResult>();
        foreach (var check in checks)
        {
            try
            {
                results.Add(await check.RunAsync(cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A check that cannot read the files it inspects (unreadable, invalid, duplicate keys)
                // has found a problem; it must not take the whole report down with it.
                results.Add(DoctorCheckResult.Error(check.GetType().Name, ex.Message.ReplaceLineEndings(" ")));
            }
        }

        var hasErrors = results.Any(r => r.Severity == DoctorSeverity.Error);
        var hasWarnings = results.Any(r => r.Severity == DoctorSeverity.Warning);

        var exitCode = hasErrors ? 1 : hasWarnings ? 2 : 0;
        return new DoctorRunResult(results, exitCode);
    }
}

public sealed record DoctorRunResult(IReadOnlyList<DoctorCheckResult> Results, int ExitCode);
