// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorLinearTimeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// The redactor runs on every tool result, more than once, on inputs up to megabytes. It must
/// stay linear on repetitive and adversarial input, and the rewrite that made it linear must not
/// have stopped masking anything the previous patterns masked.
/// </summary>
public sealed class SecretOutputRedactorLinearTimeTests
{
    // 8x the input. Linear work lands near 8 (less while the small run is still warming up);
    // a quadratic pattern lands near 64. The 24 limit leaves room for a noisy machine, and the
    // measurement is repeated before a failure counts.
    private const int SmallChars = 64 * 1024;
    private const int LargeChars = 8 * SmallChars;
    private const double MaxGrowth = 24;

    public static TheoryData<string> ShapeNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in RedactorComparisonInputs.Shapes())
            data.Add(name);
        return data;
    }

    private static double Milliseconds(string input)
    {
        var sw = Stopwatch.StartNew();
        SecretOutputRedactor.Redact(input);
        return sw.Elapsed.TotalMilliseconds;
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_time_grows_linearly_with_input_size(string shape)
    {
        var build = RedactorComparisonInputs.Shapes().Single(s => s.Name == shape).Build;
        var small = build(SmallChars);
        var large = build(LargeChars);
        Milliseconds(small); // warm up: regex construction and JIT must not count against the small run

        var growth = double.MaxValue;
        for (var attempt = 0; attempt < 3 && growth > MaxGrowth; attempt++)
        {
            // Floor the denominator so a sub-millisecond small run cannot inflate the ratio.
            growth = Milliseconds(large) / Math.Max(Milliseconds(small), 1.0);
        }

        Assert.True(growth <= MaxGrowth, $"{shape}: 8x the input took {growth:F1}x as long (linear is about 8, quadratic about 64)");
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

    // The shapes at a size the old, quadratic patterns can still handle: the output has to be
    // identical to the old redactor's, which also proves the engine change did not move a match.
    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_gives_the_old_output_on_repetitive_shapes(string shape)
    {
        var input = RedactorComparisonInputs.Shapes().Single(s => s.Name == shape).Build(24 * 1024);

        AssertSameAsOldRedactor(input);
    }

    // The shape the narrowed sk- rule requires, stated independently of the production pattern.
    private static readonly Regex RealSkKey = new(@"^sk-(?:[A-Za-z0-9]{20}|[A-Za-z0-9]{1,16}-[A-Za-z0-9_-]{40})", RegexOptions.None);

    // The one intended difference: the sk- rule no longer masks "sk-" words. Every sk- whose
    // text from there on is not key-shaped is neutralised in the input (sk- becomes Sk-), in
    // whatever run or token it sits; after that the old and new redactors must agree exactly. An
    // input with no such sk- is compared as it stands.
    private static string NeutraliseOrdinarySkWords(string input) =>
        AnySk.Replace(input, m => RealSkKey.IsMatch(input.AsSpan(m.Index).ToString()) ? m.Value : "Sk-");

    private static readonly Regex AnySk = new("sk-", RegexOptions.None);

    private static void AssertSameAsOldRedactor(string input)
    {
        var comparable = NeutraliseOrdinarySkWords(input);
        var old = PreLinearSecretOutputRedactor.Redact(comparable);
        var current = SecretOutputRedactor.Redact(comparable);
        Assert.True(old == current, $"input [{Shorten(comparable)}]\nold     [{Shorten(old)}]\ncurrent [{Shorten(current)}]");
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
