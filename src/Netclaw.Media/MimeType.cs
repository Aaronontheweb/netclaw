// -----------------------------------------------------------------------
// <copyright file="MimeType.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Media;

/// <summary>
/// Canonical MIME type value object. No implicit string conversion is provided;
/// use <see cref="Value"/> when crossing a primitive boundary.
/// </summary>
public readonly record struct MimeType
{
    public const string DefaultValue = "application/octet-stream";

    public string Value { get; }

    public MimeType(string? mimeType)
    {
        Value = MimeTypeCatalog.Normalize(mimeType);
    }

    public MimeType() : this(DefaultValue)
    {
    }

    public static MimeType Default => new(DefaultValue);

    public override string ToString() => Value;
}

/// <summary>
/// MIME metadata supplied by an untrusted transport or caller.
/// </summary>
public readonly record struct DeclaredMimeType
{
    public string Value { get; }

    public DeclaredMimeType(string? mimeType)
    {
        Value = new MimeType(mimeType).Value;
    }

    public DeclaredMimeType() : this(MimeType.DefaultValue)
    {
    }

    public override string ToString() => Value;
}

/// <summary>
/// MIME type returned by content scanning after bytes and filename validate.
/// </summary>
public readonly record struct VerifiedMimeType
{
    public MimeType MimeType { get; }

    public VerifiedMimeType(MimeType mimeType)
    {
        MimeType = mimeType;
    }

    public VerifiedMimeType(string? mimeType) : this(new MimeType(mimeType))
    {
    }

    public string Value => MimeType.Value;

    public override string ToString() => Value;
}

public readonly record struct FileExtension
{
    public string Value { get; }

    public FileExtension(string? extension)
    {
        Value = Normalize(extension);
    }

    public static FileExtension FromPath(string path) => new(Path.GetExtension(path));

    public static FileExtension Empty => new(null);

    public bool IsEmpty => Value.Length == 0;

    public override string ToString() => Value;

    private static string Normalize(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : "." + trimmed.ToLowerInvariant();
    }
}
