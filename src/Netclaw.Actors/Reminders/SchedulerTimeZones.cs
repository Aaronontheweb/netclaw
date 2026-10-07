// -----------------------------------------------------------------------
// <copyright file="SchedulerTimeZones.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Reminders;

/// <summary>
/// The platform time zone lookups that <see cref="SchedulerTimeZones"/> uses.
/// Tests replace them to simulate a host without a time zone database.
/// </summary>
public sealed record SystemTimeZones(
    Func<string, TimeZoneInfo> Find,
    Func<bool> AnyZones,
    Func<string, string?> WindowsIdToIanaId)
{
    public static SystemTimeZones Current { get; } = new(
        TimeZoneInfo.FindSystemTimeZoneById,
        () => TimeZoneInfo.GetSystemTimeZones().Count > 0,
        windowsId => TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var ianaId) ? ianaId : null);
}

/// <summary>
/// Resolves the time zone ids that the reminder scheduler accepts in a
/// <c>CRON_TZ=</c> prefix. The scheduler and every place that takes a time
/// zone from a user use this one resolution, so they cannot disagree.
/// </summary>
public static class SchedulerTimeZones
{
    private const string ExampleHint = "Use an IANA time zone id without spaces (e.g. 'Europe/Brussels').";

    /// <summary>
    /// Resolves <paramref name="zoneId"/>. On failure, <paramref name="error"/> says why: the id has spaces,
    /// the zone is unknown, or the host has no time zone database at all.
    /// </summary>
    public static bool TryResolve(
        string zoneId,
        out TimeZoneInfo zone,
        out string? error,
        SystemTimeZones? zones = null)
    {
        zones ??= SystemTimeZones.Current;
        zone = TimeZoneInfo.Utc;
        error = null;

        // The scheduler ends the zone id at the first space, so an id with spaces
        // (a Windows name such as "Eastern Standard Time") can never be used.
        if (string.IsNullOrWhiteSpace(zoneId) || zoneId.Any(char.IsWhiteSpace))
        {
            error = $"Time zone '{zoneId}' is not usable: it is empty or contains spaces. {ExampleHint}";
            return false;
        }

        try
        {
            zone = zones.Find(zoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            error = zones.AnyZones()
                ? $"Unknown time zone '{zoneId}' on this system. {ExampleHint}"
                : $"Unknown time zone '{zoneId}': this system has no time zone database (tzdata appears to be missing). " +
                  "Install it, for example 'apt-get install tzdata' on Debian or Ubuntu, then try again.";
        }
        catch (InvalidTimeZoneException)
        {
            error = $"Invalid time zone '{zoneId}' on this system. {ExampleHint}";
        }

        return false;
    }

    /// <summary>
    /// The time zone id to offer a user as a default: the local zone when the scheduler accepts it,
    /// else its IANA equivalent (a Windows zone name), else UTC.
    /// </summary>
    public static string DefaultId(TimeZoneInfo local, SystemTimeZones? zones = null)
    {
        zones ??= SystemTimeZones.Current;
        if (TryResolve(local.Id, out _, out _, zones))
            return local.Id;

        var ianaId = zones.WindowsIdToIanaId(local.Id);
        return ianaId is not null && TryResolve(ianaId, out _, out _, zones)
            ? ianaId
            : TimeZoneInfo.Utc.Id;
    }
}
