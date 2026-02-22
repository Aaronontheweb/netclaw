using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Tests for <see cref="CompactionPromptBuilder"/> structured prompt generation.
/// </summary>
public class CompactionPromptBuilderTests
{
    [Fact]
    public void BuildSummarizationSystemPrompt_contains_required_sections()
    {
        var prompt = CompactionPromptBuilder.BuildSummarizationSystemPrompt();

        Assert.Contains("Task Overview", prompt);
        Assert.Contains("Current State", prompt);
        Assert.Contains("Key Decisions", prompt);
        Assert.Contains("Pending Actions", prompt);
        Assert.Contains("Tool Usage Summary", prompt);
    }

    [Fact]
    public void BuildSummarizationUserPrompt_skips_system_messages()
    {
        var history = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.System, Content = "You are helpful." },
            new() { Role = ChatRole.User, Content = "Hello" },
            new() { Role = ChatRole.Assistant, Content = "Hi there!" }
        };

        var prompt = CompactionPromptBuilder.BuildSummarizationUserPrompt(history);

        Assert.DoesNotContain("You are helpful", prompt);
        Assert.Contains("Hello", prompt);
        Assert.Contains("Hi there!", prompt);
    }

    [Fact]
    public void BuildSummarizationUserPrompt_includes_tool_call_names()
    {
        var history = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls =
                {
                    new SerializableToolCall
                    {
                        CallId = "call-1",
                        Name = "web_search",
                        ArgumentsJson = "{}"
                    }
                }
            },
            new()
            {
                Role = ChatRole.Tool,
                Content = "Found 3 results",
                ToolCallId = "call-1",
                Name = "web_search"
            }
        };

        var prompt = CompactionPromptBuilder.BuildSummarizationUserPrompt(history);

        Assert.Contains("[Called tool: web_search]", prompt);
        Assert.Contains("Found 3 results", prompt);
    }

    [Fact]
    public void BuildMemoryExtractionSystemPrompt_contains_required_sections()
    {
        var prompt = CompactionPromptBuilder.BuildMemoryExtractionSystemPrompt();

        Assert.Contains("Key Facts", prompt);
        Assert.Contains("Action Items", prompt);
        Assert.Contains("Learned Preferences", prompt);
    }

    [Fact]
    public void BuildMemoryExtractionUserPrompt_includes_user_and_assistant_content()
    {
        var history = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.System, Content = "System prompt" },
            new() { Role = ChatRole.User, Content = "My name is Alice" },
            new() { Role = ChatRole.Assistant, Content = "Nice to meet you, Alice!" }
        };

        var prompt = CompactionPromptBuilder.BuildMemoryExtractionUserPrompt(history);

        Assert.DoesNotContain("System prompt", prompt);
        Assert.Contains("My name is Alice", prompt);
        Assert.Contains("Nice to meet you, Alice!", prompt);
    }

    [Fact]
    public void BuildSummarizationUserPrompt_empty_history_returns_header_only()
    {
        var prompt = CompactionPromptBuilder.BuildSummarizationUserPrompt([]);

        Assert.Contains("Summarize the following", prompt);
    }
}
