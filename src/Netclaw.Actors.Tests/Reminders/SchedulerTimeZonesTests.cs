// -----------------------------------------------------------------------
// <copyright file="SchedulerTimeZonesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public sealed class SchedulerTimeZonesTests
{
    private static readonly TimeZoneInfo Chicago =
        TimeZoneInfo.CreateCustomTimeZone("America/Chicago", TimeSpan.FromHours(-6), "Chicago", "Chicago");

    private static SystemTimeZones HostWith(params string[] zoneIds) => new(
        id => zoneIds.Contains(id, StringComparer.Ordinal)
            ? TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.Zero, id, id)
            : throw new TimeZoneNotFoundException(id),
        () => zoneIds.Length > 0,
        _ => null);

    private static readonly SystemTimeZones HostWithoutTzdata = HostWith();

    [Fact]
    public void Known_zone_resolves()
    {
        Assert.True(SchedulerTimeZones.TryResolve("America/Chicago", out var zone, out var error, HostWith("America/Chicago")));
        Assert.Equal("America/Chicago", zone.Id);
        Assert.Null(error);
    }

    [Fact]
    public void Unknown_zone_on_a_host_with_zones_says_the_zone_is_unknown()
    {
        Assert.False(SchedulerTimeZones.TryResolve("Mars/Olympus", out _, out var error, HostWith("America/Chicago")));
        Assert.Equal(
            "Unknown time zone 'Mars/Olympus' on this system. Use an IANA time zone id without spaces (e.g. 'Europe/Brussels').",
            error);
    }

    [Fact]
    public void Valid_zone_on_a_host_without_tzdata_says_the_database_is_missing()
    {
        Assert.False(SchedulerTimeZones.TryResolve("America/Chicago", out _, out var error, HostWithoutTzdata));
        Assert.Equal(
            "Unknown time zone 'America/Chicago': this system has no time zone database (tzdata appears to be missing). " +
            "Install it, for example 'apt-get install tzdata' on Debian or Ubuntu, then try again.",
            error);
    }

    [Fact]
    public void Invalid_zone_data_is_reported_as_invalid()
    {
        var host = new SystemTimeZones(_ => throw new InvalidTimeZoneException("corrupt"), () => true, _ => null);

        Assert.False(SchedulerTimeZones.TryResolve("Europe/Brussels", out _, out var error, host));
        Assert.Equal(
            "Invalid time zone 'Europe/Brussels' on this system. Use an IANA time zone id without spaces (e.g. 'Europe/Brussels').",
            error);
    }

    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("")]
    [InlineData(" ")]
    public void Ids_the_scheduler_cannot_carry_are_rejected_even_when_the_host_knows_them(string zoneId)
    {
        var host = new SystemTimeZones(id => Chicago, () => true, _ => null);

        Assert.False(SchedulerTimeZones.TryResolve(zoneId, out _, out var error, host));
        Assert.Contains("empty or contains spaces", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_is_the_local_zone_when_the_scheduler_accepts_it()
    {
        var local = TimeZoneInfo.CreateCustomTimeZone("America/Chicago", TimeSpan.FromHours(-6), "Chicago", "Chicago");

        Assert.Equal("America/Chicago", SchedulerTimeZones.DefaultId(local, HostWith("America/Chicago")));
    }

    [Fact]
    public void Default_converts_a_windows_zone_name_to_its_iana_id()
    {
        var local = TimeZoneInfo.CreateCustomTimeZone("Central Standard Time", TimeSpan.FromHours(-6), "Central", "Central");
        var host = HostWith("America/Chicago", "Central Standard Time") with
        {
            WindowsIdToIanaId = windowsId => windowsId == "Central Standard Time" ? "America/Chicago" : null
        };

        Assert.Equal("America/Chicago", SchedulerTimeZones.DefaultId(local, host));
    }

    [Fact]
    public void Default_is_utc_when_the_local_zone_has_no_usable_id()
    {
        var local = TimeZoneInfo.CreateCustomTimeZone("Central Standard Time", TimeSpan.FromHours(-6), "Central", "Central");

        Assert.Equal("UTC", SchedulerTimeZones.DefaultId(local, HostWith("America/Chicago")));
    }

    [Fact]
    public void Cron_prefix_with_an_unknown_zone_is_not_reported_as_a_bad_cron_expression()
    {
        var parsed = CronScheduleHelper.TryParse("CRON_TZ=Mars/Olympus 0 8 * * *", out _, out var prefixError);

        Assert.False(parsed);
        Assert.Equal(
            "Invalid CRON_TZ prefix. Unknown time zone 'Mars/Olympus' on this system. Use an IANA time zone id without spaces (e.g. 'Europe/Brussels').",
            prefixError);
    }

    [Fact]
    public void Bad_cron_fields_behind_a_good_zone_have_no_prefix_error()
    {
        var parsed = CronScheduleHelper.TryParse("CRON_TZ=UTC not a cron", out _, out var prefixError);

        Assert.False(parsed);
        Assert.Null(prefixError);
    }
}
