using System.Collections.Generic;
using System.Linq;
using Occurify;
using Occurify.Extensions;
using Occurify.Reactive.Extensions;
using Occurify.TimeZones;

namespace HomeAssistantApps.modules;

public enum Holiday
{
    None,
    Independence,
    Halloween,
    Christmas
}

/// <summary>
/// Shared definition of a holiday season and its lighting defaults.
/// </summary>
public sealed record HolidayDefinition(
    Holiday Holiday,
    string Name,
    IPeriodTimeline Period,
    IReadOnlyList<IReadOnlyList<int>>? HueColors = null,
    string? WledPresetKeyword = null,
    bool TurnOnOutdoorSwitches = false);

/// <summary>
/// Central calendar of holiday lighting seasons built on Occurify timelines.
/// </summary>
public class HolidayCalendar
{
    private readonly IReadOnlyList<HolidayDefinition> _holidays;

    public HolidayCalendar()
        : this(CreateDefaultHolidays())
    {
    }

    internal HolidayCalendar(IEnumerable<HolidayDefinition> holidays)
    {
        _holidays = holidays.ToList();
    }

    public IReadOnlyList<HolidayDefinition> Holidays => _holidays;

    public HolidayDefinition? Get(Holiday holiday) =>
        _holidays.FirstOrDefault(h => h.Holiday == holiday);

    public HolidayDefinition? GetActiveHoliday() =>
        GetActiveHoliday(DateTime.UtcNow);

    public HolidayDefinition? GetActiveHoliday(DateTime utcInstant) =>
        _holidays.FirstOrDefault(h => h.Period.ContainsInstant(utcInstant));

    public Holiday CurrentHoliday => GetActiveHoliday()?.Holiday ?? Holiday.None;

    public bool IsActive(Holiday holiday) => IsActive(holiday, DateTime.UtcNow);

    public bool IsActive(Holiday holiday, DateTime utcInstant) =>
        Get(holiday)?.Period.ContainsInstant(utcInstant) ?? false;

    /// <summary>
    /// Subscribes to start/end transitions for a specific holiday season.
    /// </summary>
    public IDisposable Subscribe(Holiday holiday, Action onStart, Action onEnd, IScheduler scheduler)
    {
        var definition = Get(holiday)
            ?? throw new ArgumentOutOfRangeException(nameof(holiday), holiday, "Unknown holiday.");

        return definition.Period.SubscribeStartEnd(onStart, onEnd, scheduler);
    }

    private static IReadOnlyList<HolidayDefinition> CreateDefaultHolidays() =>
    [
        new(
            Holiday.Independence,
            "Independence",
            Period: TimeZoneInstants.StartOfMonths(7)
                .To(TimeZoneInstants.StartOfMonths(7) + TimeSpan.FromDays(6)),
            HueColors: [[255, 0, 0], [0, 0, 255]],
            WledPresetKeyword: "Independence",
            TurnOnOutdoorSwitches: false),

        new(
            Holiday.Halloween,
            "Halloween",
            Period: (TimeZoneInstants.StartOfMonths(10) + TimeSpan.FromDays(21))
                .To(TimeZoneInstants.StartOfMonths(11) + TimeSpan.FromDays(2)),
            HueColors: [[148, 0, 211], [255, 140, 0], [255, 0, 0]],
            WledPresetKeyword: "Halloween",
            TurnOnOutdoorSwitches: false),

        new(
            Holiday.Christmas,
            "Christmas",
            Period: TimeZoneInstants.FromCron("0 0 0 ? 11 FRI#4")
                .To(TimeZoneInstants.FromCron("0 0 0 12 1 ?")),
            HueColors: [[0, 128, 0], [0, 0, 255], [255, 0, 0]],
            WledPresetKeyword: "Christmas",
            TurnOnOutdoorSwitches: true),
    ];
}
