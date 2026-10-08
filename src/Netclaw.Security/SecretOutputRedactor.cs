// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Security;

/// <summary>
/// Redacts common secret-bearing patterns before text reaches an output or log boundary.
/// This is defense-in-depth for accidental leakage; not a replacement for access controls.
/// </summary>
public static partial class SecretOutputRedactor
{
    private const int MaxSecretKeyChars = 512;
    private const string Redacted = "***REDACTED***";

    private static readonly string[] SecretKeyFragments =
    [
        "apikey",
        "token",
        "secret",
        "password",
        "authorization",
        "credential",
        "privatekey",
        "signingkey",
        "connectionstring"
    ];

    public static bool IsSecretKey(string key)
    {
        // A hostile MCP schema can supply arbitrarily large property names.
        // Fail closed instead of allocating an equally large normalized key
        // just to decide whether its value is safe to display.
        if (key.Length > MaxSecretKeyChars)
            return true;

        var normalized = Netclaw.Tools.ToolArgumentHelper.NormalizeKey(key);
        return SecretKeyFragments.Any(fragment =>
            normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ContainsSecretLikeContent(string output)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        return !string.Equals(Redact(output), output, StringComparison.Ordinal);
    }

    public static string Redact(string output)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        var sanitized = output;

        sanitized = JsonSecretValueRegex().Replace(sanitized, m =>
            $"\"{m.Groups[1].Value}\": \"{Redacted}\"");

        sanitized = ConnectionStringPasswordRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted};");

        sanitized = EnvSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}={Redacted}");

        sanitized = PrefixedEnvSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}={Redacted}");

        sanitized = HeaderSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = SlackWebhookUrlRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = KnownTokenRegex().Replace(sanitized, Redacted);

        sanitized = JwtTokenRegex().Replace(sanitized, Redacted);

        sanitized = PrivateKeyBlockRegex().Replace(sanitized, Redacted);

        return sanitized;
    }

    /// <summary>
    /// An exception surfaced from an external call (an OAuth token/DCR exchange, a webhook
    /// delivery, a subprocess) can carry the far side's raw error body inside
    /// <see cref="Exception.Message"/> or an inner exception, and that body can echo back a
    /// secret the request sent. Passing such an exception straight to a logger lets the
    /// unredacted body reach any log sink, including ones that leave the box (OTLP export).
    /// This swaps in a redacted stand-in only when secret-shaped content is actually
    /// present, so the overwhelming majority of exceptions (network errors, cancellations,
    /// disposal failures) keep their original instance, type, and full native stack trace.
    /// </summary>
    public static Exception RedactForLogging(Exception ex)
    {
        var rendered = ex.ToString();
        var redacted = Redact(rendered);
        if (string.Equals(redacted, rendered, StringComparison.Ordinal))
            return ex;

        var typeName = ex.GetType().FullName ?? ex.GetType().Name;
        return new RedactedLoggingException(
            $"{typeName} (message redacted; matched secret-shaped content): {redacted}");
    }

    private sealed class RedactedLoggingException(string message) : Exception(message);

    // The secret-bearing words shared by the JSON and env-style name rules.
    private const string SecretNameWords = "api[_-]?key|token|secret|password|passwd|authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|signing[_-]?key|private[_-]?key|connection[_-]?string|credential";

    [GeneratedRegex("\"((?:" + SecretNameWords + ")[^\"]*)\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretValueRegex();

    // Kept in lockstep with JsonSecretValueRegex's key words: an OAuth token endpoint's
    // error body is as likely to arrive form-urlencoded ("client_secret=...&grant_type=...")
    // as JSON. The secret word must sit at the start of a name (\b), e.g. TOKEN_FILE, --password.
    [GeneratedRegex("\\b((?:" + SecretNameWords + ")[A-Z0-9_-]*)=([^\\s;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvSecretValueRegex();

    // The same words after an underscore: DB_PASSWORD, AWS_SECRET_ACCESS_KEY, GITHUB_TOKEN.
    // Narrower than the rule above because ordinary settings carry these words too:
    // the word must be a whole segment (so not max_tokens, TOKENIZERS_*), must not follow
    // next/page/continuation/cursor (pagination cursors), the name must not end in a
    // descriptor (TOKEN_FILE, SECRET_ARN, PASSWORD_MIN_LENGTH, PASSWORD_STDIN ...), and a value that is a
    // variable or template reference (${{ secrets.X }}, ${X}, $X, $(cmd), %X%, optionally quoted) or an
    // empty quoted value is not a secret.
    // Only the secret word onwards is matched, so the prefix is left untouched. The tail is
    // bounded so a long run of "token_token_..." stays linear.
    [GeneratedRegex("(?<=_)(?<!(?:next|page|continuation|cursor)_)((?:" + SecretNameWords + ")(?![A-Z])(?>[A-Z0-9_-]{0,64}))"
        + "(?<!_(?:file|path|dir|name|arn|url|header|env|permissions|length|lifetime|endpoint|stdin))"
        + "=(?![\"']?(?:\\$[{(]|\\$[A-Z_]|%[A-Z_][A-Z0-9_]*%)|[\"']{2})([^\\s;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedEnvSecretValueRegex();

    [GeneratedRegex("(Authorization\\s*:\\s*Bearer\\s+)(\\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderSecretValueRegex();

    [GeneratedRegex("(https://hooks\\.slack\\.com/services/)[A-Z0-9_-]+/[A-Z0-9_-]+/[A-Z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlackWebhookUrlRegex();

    [GeneratedRegex("((?:Password|Pwd)\\s*=\\s*)[^;]+;", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPasswordRegex();

    // Credentials recognisable by a fixed prefix and structure, redacted wherever they
    // appear, whatever the surrounding name. One alternation = one pass over the text.
    // Every entry needs a documented format and a matching test in SecretOutputRedactorTests;
    // do not add entropy or "long random string" rules: they corrupt commit SHAs, UUIDs,
    // base64 content and ids that the model must echo back exactly.
    [GeneratedRegex("\\b(?:" +
        // OpenAI (sk-, sk-proj-, sk-svcacct-) and Anthropic (sk-ant-): https://platform.openai.com/docs/api-reference/authentication , https://docs.anthropic.com/en/api/getting-started
        "sk-[A-Za-z0-9_-]{8,}" +
        // Slack bot/user/app/config tokens: https://api.slack.com/authentication/token-types
        "|xox[abeprs]-[A-Za-z0-9-]{8,}|xapp-[A-Za-z0-9-]{8,}" +
        // GitHub ghp_/gho_/ghu_/ghs_/ghr_ and fine-grained github_pat_: https://github.blog/engineering/platform-security/behind-githubs-new-authentication-token-formats/
        "|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{22,}" +
        // AWS access key id (long-term AKIA, temporary ASIA), 20 chars: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_identifiers.html#identifiers-unique-ids
        "|(?:AKIA|ASIA)[A-Z0-9]{16}" +
        // Stripe live secret and restricted keys: https://docs.stripe.com/keys
        "|[rs]k_live_[A-Za-z0-9]{16,}" +
        // Stripe webhook signing secret: https://docs.stripe.com/webhooks
        "|whsec_[A-Za-z0-9]{16,}" +
        // Google API key, "AIza" + 35: https://cloud.google.com/docs/authentication/api-keys
        "|AIza[A-Za-z0-9_-]{35}" +
        // npm access token, "npm_" + 36 (length per GitHub secret scanning): https://docs.github.com/en/code-security/secret-scanning/introduction/supported-secret-scanning-patterns
        "|npm_[A-Za-z0-9]{36}" +
        // PyPI API token, a macaroon that always begins with the encoded "pypi.org" location: https://pypi.org/help/#apitoken
        "|pypi-AgEIcHlwaS5vcmc[A-Za-z0-9_-]{50,}" +
        // Discord bot token, base64(user id) . timestamp . HMAC (shape per GitHub secret scanning): https://discord.com/developers/docs/reference#authentication
        "|[MNO][A-Za-z0-9_-]{23,25}\\.[A-Za-z0-9_-]{6}\\.[A-Za-z0-9_-]{27,38}" +
        // Not \\b: a token may end in '-' or '_', which \\b would leave unredacted.
        ")(?![A-Za-z0-9_-])")]
    private static partial Regex KnownTokenRegex();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{10,}\\.eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\b")]
    private static partial Regex JwtTokenRegex();

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]+?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlockRegex();
}
