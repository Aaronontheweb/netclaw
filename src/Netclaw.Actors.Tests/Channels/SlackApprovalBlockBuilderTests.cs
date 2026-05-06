// -----------------------------------------------------------------------
// <copyright file="SlackApprovalBlockBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Slack;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackApprovalBlockBuilderTests
{
    [Fact]
    public void BuildApprovalText_uses_request_option_labels()
    {
        const string sessionLabel = "Approve in /home/user/.netclaw/logs/ for this chat";
        const string alwaysLabel = "Approve in /home/user/.netclaw/logs/ always";

        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-1",
            ToolName = "shell_execute",
            DisplayText = "grep 'error' /home/user/.netclaw/logs/app.log",
            Patterns = ["grep /home/user/.netclaw/logs/app.log"],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, sessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlways, alwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var text = SlackApprovalBlockBuilder.BuildApprovalText(request);

        Assert.Contains(sessionLabel, text);
        Assert.Contains(alwaysLabel, text);
    }
}
