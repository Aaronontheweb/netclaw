// -----------------------------------------------------------------------
// <copyright file="InitConsoleMenuPrompt.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Init;

/// <summary>
/// Console-based menu prompt used before Termina launches. The
/// existing-install menu and the start-over dialog both fit on a single
/// screen and have a small fixed number of choices, so we render them as
/// numbered plain-text prompts via <see cref="TextReader.ReadLine"/>
/// rather than booting a Termina page just to take one keystroke.
/// </summary>
/// <remarks>
/// Kept dependency-free (no Spectre.Console) so this surface can be
/// exercised in unit tests with <see cref="StringReader"/> /
/// <see cref="StringWriter"/>.
/// </remarks>
public static class InitConsoleMenuPrompt
{
    /// <summary>
    /// Show the existing-install menu and return the selected action.
    /// Returns <see cref="InitMenuAction.Cancel"/> on EOF / unparseable
    /// input so the caller can bail out without surprising side effects.
    /// </summary>
    public static InitMenuAction PromptExistingInstallMenu(TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        WriteHeader(output, "Existing installation detected. What would you like to do?");
        WriteChoices(output, InitExistingInstallMenu.Choices.Select(c => c.Label).ToArray());

        var choice = ReadChoice(input, output, InitExistingInstallMenu.Choices.Count);
        if (choice is null)
            return InitMenuAction.Cancel;

        return InitExistingInstallMenu.Choices[choice.Value].Action;
    }

    /// <summary>
    /// Show the start-over sub-dialog and return the selected action.
    /// Returns <see cref="InitStartOverAction.Cancel"/> on EOF / unparseable
    /// input.
    /// </summary>
    public static InitStartOverAction PromptStartOverDialog(TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        WriteHeader(output, "Start over from scratch — pick the scope of the reset:");
        for (var i = 0; i < InitStartOverDialog.Choices.Count; i++)
        {
            var c = InitStartOverDialog.Choices[i];
            output.WriteLine($"  {i + 1}) {c.Label}");
            output.WriteLine($"     {c.Description}");
        }

        var idx = ReadChoice(input, output, InitStartOverDialog.Choices.Count);
        if (idx is null)
            return InitStartOverAction.Cancel;

        return InitStartOverDialog.Choices[idx.Value].Action;
    }

    /// <summary>
    /// Double-confirmation prompt for a destructive start-over action.
    /// Both stages must read literal <c>yes</c> (case-insensitive) for the
    /// action to be authorized.
    /// </summary>
    public static bool ConfirmDestructiveAction(
        InitStartOverAction action, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!InitStartOverDialog.RequiresDoubleConfirmation(action))
            return true;

        output.WriteLine();
        output.WriteLine("THIS IS DESTRUCTIVE. The chosen action will permanently remove files.");
        output.WriteLine($"Action: {action}");
        output.WriteLine();
        output.Write("Type `yes` to proceed (first of two confirmations): ");
        var first = (input.ReadLine() ?? "").Trim();
        if (!string.Equals(first, "yes", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Cancelled. No changes made.");
            return false;
        }

        output.Write("Confirm again — type `yes` to PROCEED: ");
        var second = (input.ReadLine() ?? "").Trim();
        if (!string.Equals(second, "yes", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Cancelled. No changes made.");
            return false;
        }

        return true;
    }

    private static void WriteHeader(TextWriter output, string text)
    {
        output.WriteLine();
        output.WriteLine(text);
        output.WriteLine();
    }

    private static void WriteChoices(TextWriter output, IReadOnlyList<string> labels)
    {
        for (var i = 0; i < labels.Count; i++)
            output.WriteLine($"  {i + 1}) {labels[i]}");
    }

    private static int? ReadChoice(TextReader input, TextWriter output, int max)
    {
        output.WriteLine();
        output.Write($"Choice [1-{max}]: ");
        var line = input.ReadLine();
        if (line is null)
            return null;

        if (!int.TryParse(line.Trim(), out var n))
            return null;
        if (n < 1 || n > max)
            return null;

        return n - 1;
    }
}
