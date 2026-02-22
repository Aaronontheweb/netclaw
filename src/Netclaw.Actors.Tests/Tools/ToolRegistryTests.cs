using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class ToolRegistryTests
{
    [Fact]
    public void GetAllTools_returns_all_registered_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");
        registry.Register(CreateFakeTool("fetch"), "web_fetch");
        registry.Register(CreateFakeTool("run_shell"), "shell");

        var tools = registry.GetAllTools();

        Assert.Equal(3, tools.Count);
    }

    [Fact]
    public void GetToolsForGrants_filters_by_granted_categories()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");
        registry.Register(CreateFakeTool("fetch"), "web_fetch");
        registry.Register(CreateFakeTool("run_shell"), "shell");
        registry.Register(CreateFakeTool("gh_issue"), "github");

        var granted = new HashSet<string> { "web_search", "shell" };
        var tools = registry.GetToolsForGrants(granted);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "search");
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "run_shell");
    }

    [Fact]
    public void GetToolsForGrants_empty_grants_returns_nothing()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");

        var tools = registry.GetToolsForGrants(new HashSet<string>());

        Assert.Empty(tools);
    }

    [Fact]
    public void GetAllTools_empty_registry_returns_empty()
    {
        var registry = new ToolRegistry();
        Assert.Empty(registry.GetAllTools());
    }

    [Fact]
    public void Multiple_tools_in_same_grant_category_all_returned()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("mcp_memorizer_store"), "mcp:memorizer");
        registry.Register(CreateFakeTool("mcp_memorizer_search"), "mcp:memorizer");

        var granted = new HashSet<string> { "mcp:memorizer" };
        var tools = registry.GetToolsForGrants(granted);

        Assert.Equal(2, tools.Count);
    }

    private static AIFunction CreateFakeTool(string name)
    {
        return AIFunctionFactory.Create(() => "result", name);
    }
}
