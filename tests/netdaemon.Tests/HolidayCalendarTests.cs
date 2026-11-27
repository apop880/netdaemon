using HomeAssistantApps;
using Microsoft.Extensions.DependencyInjection;

namespace netdaemon.Tests;

public class HolidayCalendarTests
{
    private static DateTime LocalToUtc(int year, int month, int day, int hour = 18, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    [Fact]
    public void Christmas_IsActiveFromFourthFridayOfNovemberThroughJanuary11()
    {
        var calendar = new HolidayCalendar();

        // 2026: Nov 1 is Sunday, so 4th Friday of November is Nov 27 (Black Friday)
        Assert.False(calendar.IsActive(Holiday.Christmas, LocalToUtc(2026, 11, 26, 23, 59)));
        Assert.True(calendar.IsActive(Holiday.Christmas, LocalToUtc(2026, 11, 27, 0, 1)));
        Assert.True(calendar.IsActive(Holiday.Christmas, LocalToUtc(2026, 12, 25, 19, 0)));
        Assert.True(calendar.IsActive(Holiday.Christmas, LocalToUtc(2027, 1, 11, 23, 59)));
        Assert.False(calendar.IsActive(Holiday.Christmas, LocalToUtc(2027, 1, 12, 0, 1)));
    }

    [Fact]
    public void Halloween_And_Independence_ActiveWindowsMatchExpectedDates()
    {
        var calendar = new HolidayCalendar();

        // Independence: Jul 1 .. Jul 7 (exclusive)
        Assert.Equal(Holiday.Independence, calendar.GetActiveHoliday(LocalToUtc(2026, 7, 4, 21, 0))?.Holiday);
        Assert.Null(calendar.GetActiveHoliday(LocalToUtc(2026, 7, 8, 21, 0)));

        // Halloween: Oct 22 .. Nov 3 (exclusive)
        Assert.Null(calendar.GetActiveHoliday(LocalToUtc(2026, 10, 21, 21, 0)));
        Assert.Equal(Holiday.Halloween, calendar.GetActiveHoliday(LocalToUtc(2026, 10, 31, 20, 0))?.Holiday);
        Assert.Null(calendar.GetActiveHoliday(LocalToUtc(2026, 11, 4, 20, 0)));
    }

    [Fact]
    public void KitchenCabinetWled_SelectsChristmasPresetsOnlyDuringChristmasSeason()
    {
        var calendar = new HolidayCalendar();

        var christmasPresets = KitchenCabinetWled.SelectEveningPresets(calendar, LocalToUtc(2026, 12, 15, 19, 0));
        var octoberPresets = KitchenCabinetWled.SelectEveningPresets(calendar, LocalToUtc(2026, 10, 31, 19, 0));
        var summerPresets = KitchenCabinetWled.SelectEveningPresets(calendar, LocalToUtc(2026, 7, 4, 19, 0));
        var postSeasonPresets = KitchenCabinetWled.SelectEveningPresets(calendar, LocalToUtc(2027, 1, 15, 19, 0));

        Assert.Same(KitchenCabinetWled.ChristmasPresets, christmasPresets);
        Assert.Same(KitchenCabinetWled.EveningPresets, octoberPresets);
        Assert.Same(KitchenCabinetWled.EveningPresets, summerPresets);
        Assert.Same(KitchenCabinetWled.EveningPresets, postSeasonPresets);
    }

    [Fact]
    public void KitchenCabinetWled_AllPresetsBuildValidWledStatePayloadsAtFullBrightness()
    {
        foreach (var preset in KitchenCabinetWled.EveningPresets.Concat(KitchenCabinetWled.ChristmasPresets))
        {
            var payload = KitchenCabinetWled.BuildPresetState(preset);
            Assert.True(payload["on"]!.GetValue<bool>());
            Assert.Equal(KitchenCabinetWled.FullBrightness, payload["bri"]!.GetValue<int>());

            var seg = payload["seg"]![0]!.AsObject();
            Assert.Equal(preset.EffectId, seg["fx"]!.GetValue<int>());
            Assert.Equal(preset.PaletteId, seg["pal"]!.GetValue<int>());
            Assert.InRange(seg["sx"]!.GetValue<int>(), 0, 255);
            Assert.InRange(seg["ix"]!.GetValue<int>(), 0, 255);

            if (preset.Colors is { Length: > 0 })
            {
                var col = seg["col"]!.AsArray();
                Assert.Equal(preset.Colors.Length, col.Count);
                foreach (var entry in col)
                {
                    var rgb = entry!.AsArray();
                    Assert.Equal(3, rgb.Count);
                    foreach (var ch in rgb)
                    {
                        Assert.InRange(ch!.GetValue<int>(), 0, 255);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(true, false, false, 12, KitchenCabinetWled.LightMode.Off)]
    [InlineData(true, false, true, 9, KitchenCabinetWled.LightMode.Off)]
    [InlineData(false, true, false, 19, KitchenCabinetWled.LightMode.Evening)]
    [InlineData(false, true, true, 21, KitchenCabinetWled.LightMode.Night)]
    [InlineData(false, false, false, 23, KitchenCabinetWled.LightMode.Night)]
    [InlineData(false, false, true, 2, KitchenCabinetWled.LightMode.Night)]
    [InlineData(false, false, false, 6, KitchenCabinetWled.LightMode.Off)]
    public void KitchenCabinetWled_DetermineStartupMode_RespectsDaylightAndNightMode(
        bool isDaylight,
        bool isEvening,
        bool isNightModeOn,
        int localHour,
        KitchenCabinetWled.LightMode expected)
    {
        var actual = KitchenCabinetWled.DetermineStartupMode(isDaylight, isEvening, isNightModeOn, localHour);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, false, true, KitchenCabinetWled.LightMode.Off)]
    [InlineData(true, false, false, KitchenCabinetWled.LightMode.Off)]
    [InlineData(false, true, true, KitchenCabinetWled.LightMode.Night)]
    [InlineData(false, true, false, KitchenCabinetWled.LightMode.Evening)]
    [InlineData(false, false, true, KitchenCabinetWled.LightMode.Night)]
    [InlineData(false, false, false, KitchenCabinetWled.LightMode.Off)]
    public void KitchenCabinetWled_DetermineModeForNightModeChange_RespectsDaylight(
        bool isDaylight,
        bool isEvening,
        bool isNightModeOn,
        KitchenCabinetWled.LightMode expected)
    {
        var actual = KitchenCabinetWled.DetermineModeForNightModeChange(isDaylight, isEvening, isNightModeOn);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DependencyInjection_ResolvesDefaultHolidays()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<HolidayCalendar>()
            .BuildServiceProvider();

        var calendar = provider.GetRequiredService<HolidayCalendar>();

        Assert.NotNull(calendar.Get(Holiday.Christmas));
        Assert.Equal(3, calendar.Holidays.Count);
    }
}
