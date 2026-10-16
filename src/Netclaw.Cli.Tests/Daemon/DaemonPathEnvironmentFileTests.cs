// -----------------------------------------------------------------------
// <copyright file="DaemonPathEnvironmentFileTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

/// <summary>
/// Unit tests for the daemon PATH provisioning contract shared by install
/// (producer), doctor --fix (rehydrator), and the systemd PATH doctor check
/// (validator). <c>InstallAsync</c>/<c>UninstallAsync</c> themselves are not
/// driven here: they invoke real <c>systemctl</c>/<c>loginctl</c> against the
/// live <c>netclaw.service</c> and the real <c>~/.config</c> unit path, so
/// exercising them in-process would mutate the developer's own service. The
/// pure builders below are exactly the content those methods write and read.
/// </summary>
public sealed class DaemonPathEnvironmentFileTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ComposePathValue_InstallDirFirst_ThenCapture_ThenDedupedFloor()
        => Assert.Equal(
            "/opt/netclaw:/home/u/.dotnet:/usr/bin:/usr/local/bin:/bin:/usr/sbin:/sbin",
            DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", "/home/u/.dotnet:/usr/bin"));

    [Fact]
    public void ComposePathValue_EmptyCapture_StillHasFunctionalFloor()
    {
        // An empty PATH on the installing shell must NOT leave the daemon with installDir
        // alone — the system floor is always guaranteed so /bin/sh etc. still resolve.
        const string expected = "/opt/netclaw:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        Assert.Equal(expected, DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", null));
        Assert.Equal(expected, DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", ""));
    }

    [Fact]
    public void ComposePathValue_DropsEmptyElements()
    {
        // POSIX treats an empty PATH element as the current directory — an exec-hijack
        // vector for a daemon running `bash -c` in an agent-controlled workspace.
        var value = DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", "/a::/b:");

        Assert.DoesNotContain("::", value, StringComparison.Ordinal);
        Assert.All(value.Split(':'), Assert.NotEmpty);
        Assert.StartsWith("/opt/netclaw:/a:/b:", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ThenReadPathValue_RoundTrips()
    {
        var content = DaemonPathEnvironmentFile.Render("/opt/netclaw", "/usr/bin");

        Assert.Equal("PATH=/opt/netclaw:/usr/bin:/usr/local/bin:/bin:/usr/sbin:/sbin\n", content);
        Assert.Equal(
            "/opt/netclaw:/usr/bin:/usr/local/bin:/bin:/usr/sbin:/sbin",
            DaemonPathEnvironmentFile.ReadPathValue(content));
    }

    [Fact]
    public void ReadPathValue_NoPathAssignment_ReturnsNull()
        => Assert.Null(DaemonPathEnvironmentFile.ReadPathValue("FOO=bar\nBAZ=qux\n"));

    [Theory]
    [InlineData("/opt/netclaw:/usr/bin", "/opt/netclaw", true)]
    [InlineData("/usr/local/bin:/usr/bin", "/opt/netclaw", false)]
    public void PathContainsDirectory_MatchesExactEntry(string pathValue, string dir, bool expected)
        => Assert.Equal(expected, DaemonPathEnvironmentFile.PathContainsDirectory(pathValue, dir));

    [Fact]
    public void TryGetInstallDir_FromExecStart_StripsBinaryAndArgs()
    {
        var lines = new[] { "[Service]", "ExecStart=/opt/netclaw/netclawd --foreground" };

        Assert.True(DaemonPathEnvironmentFile.TryGetInstallDir(lines, out var dir));
        Assert.Equal("/opt/netclaw", dir);
    }

    [Fact]
    public void TryGetInstallDir_NoExecStart_ReturnsFalse()
        => Assert.False(DaemonPathEnvironmentFile.TryGetInstallDir(new[] { "[Service]" }, out _));

    [Fact]
    public void TryGetEnvironmentFilePath_StripsTolerantDashPrefix()
    {
        var lines = new[] { "EnvironmentFile=-/home/u/.netclaw/config/daemon.env" };

        Assert.True(DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(lines, out var path));
        Assert.Equal("/home/u/.netclaw/config/daemon.env", path);
    }

    [Fact]
    public void TryGetEnvironmentFilePath_Absent_ReturnsFalse()
        => Assert.False(DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(
            new[] { "ExecStart=/opt/netclaw/netclawd" }, out _));

    [Fact]
    public void BuildDaemonUnitContent_WiresEnvironmentFile_AndOmitsInlinePath()
    {
        var unit = DaemonManager.BuildDaemonUnitContent(
            "/opt/netclaw/netclawd",
            "/opt/netclaw/netclaw",
            "/home/u/.netclaw/config/daemon.env");

        Assert.Contains("EnvironmentFile=-/home/u/.netclaw/config/daemon.env", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment=PATH=", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/opt/netclaw/netclawd", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStop=/opt/netclaw/netclaw daemon stop", unit, StringComparison.Ordinal);

        // Producer/consumer contract (netclaw-dev/netclaw#1665): the generated unit's
        // TimeoutStopSec= must track DaemonConfig.SystemdTimeoutStopSec, not a stale literal,
        // so systemd never SIGKILLs the cgroup out from under a still-legitimately-waiting
        // `netclaw daemon stop` (ExecStop=).
        Assert.Contains(
            $"TimeoutStopSec={(int)DaemonConfig.SystemdTimeoutStopSec.TotalSeconds}",
            unit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveDaemonEnvironmentFile_DeletesFile_AndIsIdempotent()
    {
        // Covers the uninstall env-file-removal contract without driving the
        // systemctl-coupled UninstallAsync.
        var paths = NewPaths();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, "PATH=/opt/netclaw:/usr/bin\n");
        var manager = new DaemonManager(paths, TimeProvider.System);

        Assert.Null(manager.RemoveDaemonEnvironmentFile());
        Assert.False(File.Exists(paths.DaemonEnvironmentFilePath));

        // Idempotent — a second call on an already-removed file must not throw.
        Assert.Null(manager.RemoveDaemonEnvironmentFile());
    }

    [Theory]
    [InlineData("# set by hand\nHTTP_PROXY=http://proxy.example:3128\nPATH=/usr/bin\n")]
    [InlineData("NETCLAW_HOME=/x/home\n")]
    public void RemoveDaemonEnvironmentFile_keeps_file_with_operator_lines_and_reports_path(string content)
    {
        var paths = NewPaths();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, content);
        var manager = new DaemonManager(paths, TimeProvider.System);

        var kept = manager.RemoveDaemonEnvironmentFile();

        Assert.Equal(paths.DaemonEnvironmentFilePath, kept);
        Assert.Equal(content, File.ReadAllText(paths.DaemonEnvironmentFilePath));
    }

    [Fact]
    public void RemoveDaemonEnvironmentFile_deletes_file_with_only_path_comments_and_blank_lines()
    {
        var paths = NewPaths();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, "# netclaw\n\n  PATH=/usr/bin\n; note\n");
        var manager = new DaemonManager(paths, TimeProvider.System);

        Assert.Null(manager.RemoveDaemonEnvironmentFile());
        Assert.False(File.Exists(paths.DaemonEnvironmentFilePath));
    }

    // ── Merge: operator lines survive a rewrite ──

    private const string ExpectedPath = "PATH=/opt/netclaw:/usr/bin:/usr/local/bin:/bin:/usr/sbin:/sbin";

    private static string Merge(string? existing, params (string Key, string Value)[] assignments)
        => DaemonPathEnvironmentFile.Merge(
            existing,
            "/opt/netclaw",
            "/usr/bin",
            assignments.Select(a => new DaemonEnvAssignment(a.Key, a.Value)).ToArray());

    [Fact]
    public void Merge_without_existing_text_equals_Render()
        => Assert.Equal(DaemonPathEnvironmentFile.Render("/opt/netclaw", "/usr/bin"), Merge(null));

    [Fact]
    public void Merge_keeps_operator_lines_comments_and_blank_lines_and_replaces_path_in_place()
    {
        const string existing = "NETCLAW_HOME=/x/home\n# proxy\nHTTP_PROXY=http://proxy.example:3128\n\nPATH=/old\nFOO=bar\n";

        var merged = Merge(existing);

        Assert.Equal(
            $"NETCLAW_HOME=/x/home\n# proxy\nHTTP_PROXY=http://proxy.example:3128\n\n{ExpectedPath}\nFOO=bar\n",
            merged);
    }

    [Fact]
    public void Merge_drops_a_duplicate_path_line()
        => Assert.Equal(
            $"A=1\n{ExpectedPath}\nB=2\n",
            Merge("A=1\nPATH=/old\nB=2\nPATH=/older\n"));

    [Fact]
    public void Merge_adds_path_at_the_end_when_none_exists()
        => Assert.Equal($"A=1\n# c\n{ExpectedPath}\n", Merge("A=1\n# c\n"));

    [Fact]
    public void Merge_adds_a_missing_final_newline()
        => Assert.Equal($"A=1\n{ExpectedPath}\n", Merge("A=1\nPATH=/old"));

    [Fact]
    public void Merge_keeps_crlf_endings_and_is_idempotent()
    {
        const string existing = "A=1\r\nPATH=/old\r\nB=2\r\n";

        var merged = Merge(existing);

        Assert.Equal($"A=1\r\n{ExpectedPath}\r\nB=2\r\n", merged);
        Assert.Equal(merged, Merge(merged));
    }

    [Fact]
    public void Merge_is_idempotent_for_a_file_with_all_forms()
    {
        var once = Merge("A=1\nPATH=/old\nPATH=/older", ("NETCLAW_X", "1"));

        Assert.Equal(once, Merge(once, ("NETCLAW_X", "1")));
    }

    [Fact]
    public void Merge_replaces_an_existing_flag_key_in_place_and_adds_a_new_key_after_the_last_line()
    {
        const string existing = "NETCLAW_DAEMON__PORT=1\nA=1\nPATH=/old\nNETCLAW_DAEMON__PORT=2\n";

        var merged = Merge(existing, ("NETCLAW_DAEMON__PORT", "56114"), ("NETCLAW_HOME", "/x/home"));

        Assert.Equal(
            $"NETCLAW_DAEMON__PORT=56114\nA=1\n{ExpectedPath}\nNETCLAW_HOME=/x/home\n",
            merged);
    }

    [Fact]
    public void Merge_writes_path_before_new_keys_in_a_fresh_file()
        => Assert.Equal(
            $"{ExpectedPath}\nNETCLAW_DAEMON__PORT=56114\nNETCLAW_HOME=/x/home\n",
            Merge(null, ("NETCLAW_DAEMON__PORT", "56114"), ("NETCLAW_HOME", "/x/home")));

    [Fact]
    public void Merge_matches_a_flag_key_only_by_the_text_before_the_first_equals()
    {
        // NETCLAW_HOME_EXTRA shares a prefix with NETCLAW_HOME and must stay.
        var merged = Merge("NETCLAW_HOME_EXTRA=keep\nNETCLAW_HOME=old\n", ("NETCLAW_HOME", "/new"));

        Assert.Equal($"NETCLAW_HOME_EXTRA=keep\nNETCLAW_HOME=/new\n{ExpectedPath}\n", merged);
    }

    // ── --env entry parsing ──

    [Fact]
    public void TryParseAll_accepts_netclaw_keys_and_keeps_the_value_verbatim()
    {
        var ok = DaemonEnvAssignment.TryParseAll(
            ["NETCLAW_DAEMON__PORT=56114", "NETCLAW_HOME=/x/a=b c", "NETCLAW_EMPTY="],
            out var assignments,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(
            [
                new DaemonEnvAssignment("NETCLAW_DAEMON__PORT", "56114"),
                new DaemonEnvAssignment("NETCLAW_HOME", "/x/a=b c"),
                new DaemonEnvAssignment("NETCLAW_EMPTY", string.Empty),
            ],
            assignments);
    }

    [Theory]
    [InlineData("NOEQUALS")]
    [InlineData("HTTP_PROXY=x")]
    [InlineData("netclaw_home=/x")]
    [InlineData("NETCLAW_=x")]
    [InlineData("NETCLAW_A B=x")]
    [InlineData("=x")]
    [InlineData("NETCLAW_HOME=/x\ny")]
    [InlineData("NETCLAW_HOME=/x\ry")]
    public void TryParseAll_refuses_bad_entries(string entry)
    {
        Assert.False(DaemonEnvAssignment.TryParseAll([entry], out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParseAll_refuses_the_same_key_twice()
        => Assert.False(DaemonEnvAssignment.TryParseAll(
            ["NETCLAW_HOME=/a", "NETCLAW_HOME=/b"], out _, out _));

    [Theory]
    [InlineData("NETCLAW_Providers__main__ApiKey=abc-secret-value")]
    [InlineData("NETCLAW_Slack__BotToken=xoxb-secret-value")]
    [InlineData("NETCLAW_Memory__ServerUrl=https://user:pw-secret-value@host.example/db")]
    public void TryParseAll_refuses_credentials_names_secrets_set_and_never_prints_the_value(string entry)
    {
        var value = entry[(entry.IndexOf('=') + 1)..];

        Assert.False(DaemonEnvAssignment.TryParseAll([entry], out _, out var error));
        Assert.Contains("netclaw secrets set", error, StringComparison.Ordinal);
        Assert.DoesNotContain(value, error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", error, StringComparison.Ordinal);
    }

    private NetclawPaths NewPaths()
    {
        var paths = new NetclawPaths(Path.Combine(_temp.Path, Guid.NewGuid().ToString("N")));
        paths.EnsureDirectoriesExist();
        return paths;
    }
}
