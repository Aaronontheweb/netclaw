// -----------------------------------------------------------------------
// <copyright file="ToolAccessPolicyRequiredDependenciesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The shell deny-list (<see cref="ShellCommandPolicy"/>) and the protected-path
/// policy (<see cref="ToolPathPolicy"/>) are security controls. Today they are
/// optional constructor parameters, so a caller that omits them gets a policy
/// that silently allows what those controls would block:
///   - a null <c>_shellCommandPolicy</c> skips the hard-deny gate (line ~138);
///   - a null <c>_toolPathPolicy</c> makes the protected-path check
///     (<c>_toolPathPolicy?.CommandReferencesDeniedPath(...) == true</c>, line ~151)
///     evaluate to false and pass.
/// Two in-repo fallbacks build the policy exactly this way — DispatchingToolExecutor
/// and SubAgentActor — so a sub-agent can run shell commands against protected
/// paths with the deny silently disabled. These deps must be required: the type
/// system, not a call-site convention, should guarantee the checks are present.
/// </summary>
public sealed class ToolAccessPolicyRequiredDependenciesTests
{
    private static ToolConfig ShellConfig()
        => new() { ShellMode = ShellExecutionMode.HostAllowed };

    private static EffectivePolicyDefaults Defaults()
        => new(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false);

    private static ToolExecutionContext PersonalContext()
        => TestToolExecutionContext.CreateBound(
            "signalr/thread-1",
            null,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

    private static INetclawTool ShellTool()
        => new ShellTool(ShellConfig(), new ToolPathPolicy([]), new ShellCommandPolicy());

    [Fact]
    public void Constructor_requires_shell_command_policy()
    {
        // A null deny-list must be rejected, not silently accepted — otherwise
        // the hard-deny gate is disabled for every command.
        Assert.Throws<ArgumentNullException>(() =>
            new ToolAccessPolicy(ShellConfig(), Defaults(), null!, new ToolPathPolicy([])));
    }

    [Fact]
    public void Constructor_requires_tool_path_policy()
    {
        // A null protected-path policy must be rejected — otherwise the
        // protected-path deny silently passes (the SubAgentActor fallback shape).
        Assert.Throws<ArgumentNullException>(() =>
            new ToolAccessPolicy(ShellConfig(), Defaults(), new ShellCommandPolicy(), null!));
    }

    [Fact]
    public void Shell_referencing_protected_path_is_denied_when_dependencies_are_wired()
    {
        // Positive control: with the protected-path policy present, a shell
        // command that touches a denied path is denied. This is the behavior the
        // fallback constructions silently lose.
        var deniedRoot = Path.Combine(Path.GetTempPath(), "netclaw-protected-root");
        var policy = new ToolAccessPolicy(
            ShellConfig(),
            Defaults(),
            new ShellCommandPolicy(),
            toolPathPolicy: new ToolPathPolicy([deniedRoot]));

        var args = ToolInput.Create("Command", $"cat {Path.Combine(deniedRoot, "secret.txt")}");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.False(decision.Allowed);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
    }
}
