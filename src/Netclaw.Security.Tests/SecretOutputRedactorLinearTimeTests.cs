// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorLinearTimeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// The redactor runs on every tool result, more than once, on inputs up to megabytes. It must
/// stay linear on repetitive and adversarial input, and the change that made it linear must not
/// have moved a single match: the output is compared byte for byte with the previous redactor.
/// </summary>
[Collection(RedactorTimingCollection.Name)]
public sealed class SecretOutputRedactorLinearTimeTests
{
    // The quadratic patterns needed seconds per megabyte (minutes for a private key); linear
    // work needs well under a second per 4 MB even on a loaded machine. The budget is generous
    // so scheduling noise cannot trip it, and far below what any quadratic shape can reach.
    private const int AdversarialChars = 4 * 1024 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    public static TheoryData<string> ShapeNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in RedactorComparisonInputs.Shapes())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_finishes_adversarial_input_within_budget(string shape)
    {
        var input = RedactorComparisonInputs.Shapes().Single(s => s.Name == shape).Build(AdversarialChars);
        SecretOutputRedactor.Redact(input[..1024]); // regex construction is a one-off cost, not the subject here

        // A quadratic pattern cannot be interrupted, so run it on a background thread and stop waiting.
        var worker = new Thread(() => SecretOutputRedactor.Redact(input)) { IsBackground = true };
        worker.Start();
        var finished = worker.Join(Budget);

        Assert.True(finished, $"{shape}: {AdversarialChars / 1024 / 1024} MB did not finish within {Budget.TotalSeconds} s");
    }

    [Fact]
    public void Redact_masks_a_private_key_that_follows_a_run_of_unterminated_headers()
    {
        var input = string.Concat(Enumerable.Repeat("-----BEGIN PRIVATE KEY-----", 200))
            + "\n-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK\n-----END RSA PRIVATE KEY-----\ntail";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain("MIIBOgIBAAJBAK", redacted, StringComparison.Ordinal);
        Assert.EndsWith("***REDACTED***\ntail", redacted, StringComparison.Ordinal);
    }

    // The shapes at a size the old, quadratic patterns can still handle.
    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_gives_the_old_output_on_repetitive_shapes(string shape)
    {
        var input = RedactorComparisonInputs.Shapes().Single(s => s.Name == shape).Build(24 * 1024);

        AssertSameAsOldRedactor(input);
    }

    private static void AssertSameAsOldRedactor(string input)
    {
        var old = PreLinearSecretOutputRedactor.Redact(input);
        var current = SecretOutputRedactor.Redact(input);
        Assert.True(old == current, $"input [{Shorten(input)}]\nold     [{Shorten(old)}]\ncurrent [{Shorten(current)}]");
    }

    private static string Shorten(string s) => (s.Length > 300 ? s[..300] + "..." : s).Replace("\n", "\\n");

    [Fact]
    public void Redact_agrees_with_the_old_redactor_on_generated_inputs()
    {
        var checkedInputs = 0;
        var masked = 0;
        foreach (var input in RedactorComparisonInputs.Random(seed: 2401, count: 120_000))
        {
            AssertSameAsOldRedactor(input);
            checkedInputs++;
            if (!string.Equals(SecretOutputRedactor.Redact(input), input, StringComparison.Ordinal))
                masked++;
        }

        // A comparison of inputs nothing happens to proves nothing.
        Assert.Equal(120_000, checkedInputs);
        Assert.True(masked > 20_000, $"only {masked} generated inputs contained anything to mask");
    }
}

/// <summary>Timing tests must not share the machine with other test classes running in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedactorTimingCollection
{
    public const string Name = "RedactorTiming";
}
