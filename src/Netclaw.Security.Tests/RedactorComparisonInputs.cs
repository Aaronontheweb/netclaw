// -----------------------------------------------------------------------
// <copyright file="RedactorComparisonInputs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security.Tests;

/// <summary>
/// Inputs for comparing the current redactor with <see cref="PreLinearSecretOutputRedactor"/>:
/// seeded random runs of secret-bearing and ordinary fragments, and the repetitive shapes that
/// made the old patterns quadratic. Credential-shaped values are assembled at run time.
/// </summary>
internal static class RedactorComparisonInputs
{
    private static string Pad(string prefix, int length, char fill) => prefix + new string(fill, length);

    private static readonly string[] Fragments =
    [
        // known token formats, real-shaped
        Pad("sk-", 48, 'k'), Pad("sk-", 20, 'q'), Pad("sk-", 30, 'k') + "-", "sk-proj-" + new string('k', 40) + "_" + new string('K', 20),
        "sk-ant-api03-" + new string('Z', 40), "xoxb-1234567890-1234567890123-" + new string('c', 24), "xapp-1-A0123456789-1234567890123-" + new string('e', 40),
        Pad("ghp_", 36, 'a'), Pad("gho_", 20, 'B'), "github_pat_" + new string('1', 11) + "_" + new string('b', 59), "AKIA" + new string('A', 16), "ASIA" + new string('B', 16),
        Pad("sk_live_", 24, 'G'), Pad("whsec_", 32, 'W'), Pad("AIza", 35, 'x'), Pad("npm_", 36, 'n'), Pad("pypi-AgEIcHlwaS5vcmc", 60, 'p'),
        "MTAxMDEwMTAxMDEwMTAxMDEw.GabcDE." + new string('d', 27),
        "eyJ" + new string('a', 12) + ".eyJ" + new string('b', 12) + "." + new string('c', 12),
        "https://hooks.slack.com/services/T0ABCDEF/B0ABCDEF/" + new string('x', 24),
        // private keys, whole and truncated
        "-----BEGIN RSA PRIVATE KEY-----", "MIIBOgIBAAJBAK", "-----END RSA PRIVATE KEY-----", "-----BEGIN PRIVATE KEY-----", "-----END PRIVATE KEY-----", "-----BEGIN CERTIFICATE-----",
        // secret names, values and structure
        "token", "TOKEN", "api_key", "API-KEY", "client_secret", "password", "Password", "Pwd", "DB_PASSWORD", "GITHUB_TOKEN", "AWS_SECRET_ACCESS_KEY", "next_page_token",
        "max_tokens", "MAX_TOKENS", "TOKENIZERS_PARALLELISM", "TOKENS_USED", "TOKEN_COUNT", "tokens", "ACCESS_TOKEN", "API_TOKEN", "token1", "TOKEN_FILE", "PASSWORD_STDIN", "authorization", "Authorization", "Bearer", "connection_string", "secret_santa", "x_secret",
        "plainvalue", "hunter2", "abc-def_123", "wJalrXUtnFEMI/K7MDENG", "true", "null", "${X}", "$HOME", "%X%", "''", "\"\"",
        "=", ":", ": ", " = ", ";", "&", ",", "\"", "'", "(", ")", "[", "]", "{", "}", "-", "_", ".", "/", "\\", "\n", " ", "  ", "\t", "--",
        // ordinary text, including things the sk- rule used to eat
        "sk-learn-contrib", "class=\"sk-top-container\"", "task-sk-1234567890", "risk-averse", "sk-toggleable__label-arrow", "sk-", "sk", "sk-short", "pip install scikit-learn",
        "the quick brown fox", "INFO started id=1234", "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=", "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
        "/usr/local/bin", "Microsoft.Extensions.Hosting.Abstractions.dll", "ghp_", "AKIA", "xoxb-", "eyJ", "xox", "github_pat_", "M", "N", "O",
    ];

    /// <summary>Seeded random concatenations of fragments, 1 to 14 per input.</summary>
    public static IEnumerable<string> Random(int seed, int count)
    {
        var random = new Random(seed);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Clear();
            var parts = random.Next(1, 15);
            for (var p = 0; p < parts; p++)
                sb.Append(Fragments[random.Next(Fragments.Length)]);
            yield return sb.ToString();
        }
    }

    /// <summary>Repetitive shapes, each as a generator of roughly the requested number of characters.</summary>
    public static IEnumerable<(string Name, Func<int, string> Build)> Shapes()
    {
        static string Rep(string unit, int chars) => string.Concat(Enumerable.Repeat(unit, Math.Max(1, chars / unit.Length)));

        var units = new[]
        {
            "token-", "token_", "sk-", "sk-abcdefghi ", "ghp_", "xoxb-", "AKIA", "Bearer ", "Authorization: Bearer ", "-----BEGIN PRIVATE KEY-----",
            "-----BEGIN RSA PRIVATE KEY-----\n", "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n", "password=", "Password=a ", "x_secret=", "DB_PASSWORD=a ",
            "eyJaaaaaaaaaaaa-", "eyJaaaaaaaaaaaa.eyJbbbbbbbbbbbb-", "\"token\":\"", "\"token\"  ", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=\n", "MNO-",
            "https://hooks.slack.com/services/A/", "AIzaaaaaaaaaaaaaaaaaaa-", "pypi-AgEIcHlwaS5vcmc-", "some ordinary log text 12345 ", "INFO ok id=1234 path=/a/b\n",
            "api_key", "ghp_" + new string('a', 30) + "-", "sk-" + new string('k', 25) + " ",
        };

        foreach (var unit in units)
            yield return ("repeat " + unit.Replace("\n", "\\n"), chars => Rep(unit, chars));

        yield return ("equals", chars => new string('=', chars));
        yield return ("dashes", chars => new string('-', chars));
        yield return ("letters", chars => new string('a', chars));
        yield return ("spaces", chars => new string(' ', chars));
        yield return ("token then spaces", chars => "token" + new string(' ', chars));
        yield return ("Authorization then spaces", chars => "Authorization" + new string(' ', chars));
        yield return ("BEGIN without END", chars => "-----BEGIN PRIVATE KEY-----" + new string('A', chars));
        yield return ("password= then no semicolon", chars => "Server=db;Password=" + new string('p', chars));
    }
}
