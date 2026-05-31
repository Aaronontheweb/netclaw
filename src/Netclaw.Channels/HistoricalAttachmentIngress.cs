// -----------------------------------------------------------------------
// <copyright file="HistoricalAttachmentIngress.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;

namespace Netclaw.Channels;

/// <summary>
/// Shared security core for historical (thread-backfill) attachment ingress
/// across Slack, Discord, and Mattermost. The scan → verified-MIME →
/// verified-category gate is identical for every channel and for both the
/// freshly-downloaded and the already-cached file, so it lives here once.
/// Channels keep only what genuinely differs — URL/auth trust and the download
/// mechanism. Centralizing this is what closes the class of channel-specific
/// bug where one path (notably the inbox cache-hit) skips the scan and serves
/// the unverified declared MIME.
/// </summary>
public static class HistoricalAttachmentIngress
{
    /// <summary>
    /// Canonical historical-attachment rejection note. The LLM sees this in
    /// place of the attachment so rejections are never silent.
    /// </summary>
    public static TextContent BuildRejected(string detail)
        => new($"[attachment rejected: {detail}]");

    public abstract record ScanOutcome
    {
        public sealed record Verified(MimeType MimeType, AttachmentCategory Category) : ScanOutcome;

        public sealed record Rejected(TextContent Note) : ScanOutcome;
    }

    /// <summary>
    /// Scans a file already on disk (freshly downloaded staging file or a
    /// previously-cached inbox file) and enforces the verified-MIME and
    /// verified-category gates. Does NOT delete the file — the caller owns the
    /// staging-file lifecycle, since a cache hit must not delete the inbox copy.
    /// </summary>
    public static async Task<ScanOutcome> ScanAndVerifyAsync(
        IContentScanner scanner,
        string filePath,
        string filename,
        DeclaredMimeType declaredMimeType,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        TimeSpan scanTimeout,
        ILogger logger,
        string channelLabel,
        CancellationToken cancellationToken)
    {
        var verification = await ContentVerification.ResolveAsync(
            scanner, filePath, filename, declaredMimeType, policy, scanTimeout, cancellationToken);

        switch (verification)
        {
            case ContentVerificationResult.Verified verified:
                return new ScanOutcome.Verified(verified.MimeType, verified.Category);

            case ContentVerificationResult.ScanThrew st:
                logger.LogWarning(st.Exception, "Historical {Channel} attachment scan threw for {Name}", channelLabel, filename);
                return new ScanOutcome.Rejected(BuildRejected(
                    $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be scanned"));

            case ContentVerificationResult.ScanBlocked sb:
                logger.LogWarning(
                    "Historical {Channel} attachment {Name} rejected by scanner: {Error} {Message}",
                    channelLabel, filename, sb.Error?.ToString(), sb.Message ?? string.Empty);
                return new ScanOutcome.Rejected(BuildRejected(
                    $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" was rejected by content scanning: {AttachmentIngressFormatting.EscapeQuoted(sb.Message ?? sb.Error?.ToString() ?? "unknown error")}"));

            case ContentVerificationResult.MissingVerifiedMime:
                logger.LogWarning(
                    "Historical {Channel} attachment {Name} rejected: scanner did not return verified MIME",
                    channelLabel, filename);
                return new ScanOutcome.Rejected(BuildRejected(
                    $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be verified by content scanning"));

            default:
                var notAllowed = (ContentVerificationResult.CategoryNotAllowed)verification;
                logger.LogWarning(
                    "Historical {Channel} attachment {Name} rejected: verified category {Category} not allowed for {Audience}",
                    channelLabel, filename, notAllowed.Category, audience);
                return new ScanOutcome.Rejected(BuildRejected(
                    $"historical attachment ({notAllowed.MimeType.Value}) category not allowed in {audience}"));
        }
    }
}
