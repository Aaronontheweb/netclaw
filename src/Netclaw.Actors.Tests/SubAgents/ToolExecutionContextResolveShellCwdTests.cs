// -----------------------------------------------------------------------
// <copyright file="ToolExecutionContextResolveShellCwdTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Locks in the <see cref="ToolExecutionContext.ResolveShellCwd"/> fallback
/// order, including the inherited-Cwd last-resort branch added so a
/// sub-agent's parent-cwd snapshot survives <c>ToolAccessPolicy</c>'s
/// per-call re-resolve when neither <c>ProjectDirectory</c> nor
/// <c>SessionDirectory</c> is available.
/// </summary>
public class ToolExecutionContextResolveShellCwdTests
{
    [Fact]
    public void Explicit_arg_wins_over_all_other_sources()
    {
        var context = new ToolExecutionContext("sess", "/tmp/sess")
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = "/home/user/repos/foo",
            Cwd = "/home/user/repos/inherited",
        };

        Assert.Equal("/explicit/arg", context.ResolveShellCwd("/explicit/arg"));
    }

    [Fact]
    public void ProjectDirectory_wins_over_session_directory_and_cwd()
    {
        var context = new ToolExecutionContext("sess", "/tmp/sess")
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = "/home/user/repos/foo",
            Cwd = "/home/user/repos/inherited",
        };

        Assert.Equal("/home/user/repos/foo", context.ResolveShellCwd(null));
    }

    [Fact]
    public void SessionDirectory_wins_over_inherited_cwd()
    {
        var context = new ToolExecutionContext("sess", "/tmp/sess")
        {
            Audience = TrustAudience.Personal,
            Cwd = "/home/user/repos/inherited",
        };

        Assert.Equal("/tmp/sess", context.ResolveShellCwd(null));
    }

    [Fact]
    public void Inherited_cwd_is_last_resort_fallback_before_null()
    {
        // Sub-agent path: parent had no ProjectDirectory, no SessionDirectory,
        // but its resolved cwd at spawn time was captured as ParentCwd and
        // placed on the child's Cwd. Without the Cwd fallback in
        // ResolveShellCwd, ToolAccessPolicy's per-call re-resolve would null
        // out the inherited snapshot and render "(no working directory)" in
        // approval prompts.
        var context = new ToolExecutionContext("sess", sessionDirectory: null)
        {
            Audience = TrustAudience.Personal,
            Cwd = "/home/user/repos/inherited",
        };

        Assert.Equal("/home/user/repos/inherited", context.ResolveShellCwd(null));
    }

    [Fact]
    public void Returns_null_when_no_source_is_available()
    {
        var context = new ToolExecutionContext("sess", sessionDirectory: null)
        {
            Audience = TrustAudience.Personal,
        };

        Assert.Null(context.ResolveShellCwd(null));
    }
}
