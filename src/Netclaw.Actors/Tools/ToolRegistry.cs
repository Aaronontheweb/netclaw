using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Registration entry pairing a tool with its ACL grant category.
/// </summary>
public sealed record ToolRegistration(AITool Tool, string GrantCategory);

/// <summary>
/// Registers <see cref="AITool"/> definitions with grant categories for policy filtering.
/// Sessions receive only tools whose grant category is in the session's allowed set.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<ToolRegistration> _tools = new();

    public void Register(AITool tool, string grantCategory)
    {
        _tools.Add(new ToolRegistration(tool, grantCategory));
    }

    /// <summary>All registered tools regardless of grants.</summary>
    public IReadOnlyList<AITool> GetAllTools() =>
        _tools.Select(t => t.Tool).ToList();

    /// <summary>Only tools whose grant category is in the allowed set.</summary>
    public IReadOnlyList<AITool> GetToolsForGrants(IReadOnlySet<string> grantedCategories) =>
        _tools
            .Where(t => grantedCategories.Contains(t.GrantCategory))
            .Select(t => t.Tool)
            .ToList();
}
