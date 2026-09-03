// -----------------------------------------------------------------------
// <copyright file="SessionLogPath.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// A canonical absolute path for one session log writer.
/// </summary>
public readonly record struct SessionLogPath
{
    /// <summary>Creates a canonical absolute session log path.</summary>
    /// <param name="value">The canonical absolute path.</param>
    public SessionLogPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("A session log path must be absolute.", nameof(value));

        var canonical = Path.GetFullPath(value);
        if (!string.Equals(value, canonical, StringComparison.Ordinal))
            throw new ArgumentException("A session log path must be canonical.", nameof(value));

        Value = canonical;
    }

    /// <summary>Gets the canonical absolute path.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
