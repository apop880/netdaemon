using System.Collections.Generic;
using System.Linq;
using Occurify;
using Occurify.Astro;
using Occurify.Extensions;
using Occurify.Reactive.Extensions;
using Occurify.TimeZones;

namespace HomeAssistantApps;

[NetDaemonApp]
public class OutsideLightsApp
{
    private readonly ILogger<OutsideLightsApp> _logger;
    private readonly Entities _entities;
    private readonly OutsideLightsConfig _config;
    private readonly IScheduler _scheduler;
    private readonly Telegram _telegram;
    private readonly IPeriodTimeline lightOnPeriod;


    public OutsideLightsApp(ILogger<OutsideLightsApp> logger, IScheduler scheduler, Entities entities, Telegram telegram)
    {
        _logger = logger;
        _entities = entities;
        _scheduler = scheduler;
        _telegram = telegram;
        _config = new OutsideLightsConfig
        {
            {
                "Independence",
                new HolidaySetting
                {
                    Hue = new List<List<int>> { new List<int> { 255, 0, 0 }, new List<int> { 0, 0, 255 } },
                    Wled = "Independence",
                    TurnOnSwitches = false,
                    Period = TimeZoneInstants.StartOfMonths(7)
                        .To(TimeZoneInstants.StartOfMonths(7) + TimeSpan.FromDays(6))
                }
            },
            {
                "Christmas",
                new HolidaySetting
                {
                    Hue = new List<List<int>> { new List<int> { 0, 128, 0 }, new List<int> { 0, 0, 255 }, new List<int> { 255, 0, 0 } },
                    Wled = "Christmas",
                    TurnOnSwitches = true,
                    Period = TimeZoneInstants.FromCron("0 0 0 ? 11 FRI#4")
                        .To(TimeZoneInstants.FromCron("0 0 0 12 1 ?"))
                }
            },
            {
                "Halloween",
                new HolidaySetting
                {
                    Hue = new List<List<int>> { new List<int> { 148, 0, 211 }, new List<int> { 255, 140, 0 }, new List<int> { 255, 0, 0 } },
                    Wled = "Halloween",
                    TurnOnSwitches = false,
                    Period = (TimeZoneInstants.StartOfMonths(10) + TimeSpan.FromDays(21))
                        .To(TimeZoneInstants.StartOfMonths(11) + TimeSpan.FromDays(2))
                }
            }
        };

        lightOnPeriod =
            (AstroInstants.LocalSunsets - TimeSpan.FromMinutes(15))
            .To(TimeZoneInstants.DailyAt(hour: 23, minute: 30));

        lightOnPeriod.SubscribeStartEnd(() => TurnLightsOn(), () => TurnLightsOff(), scheduler);
    }

    private void TurnLightsOn()
    {
        int colorPos = 0;
        int wledEffectPos = 0;
        _logger.LogInformation("Turning on outside lights.");
        string lightMode = "Standard";
        HolidaySetting? holiday = null;
        _entities.Light.WledPorch.TurnOn();

        foreach (var h in _config)
        {
            if (h.Value.Period?.IsNow() ?? false)
            {
                lightMode = h.Key;
                holiday = h.Value;
                break;
            }
        }

        if (lightMode == "Standard")
        {
            _entities.Light.Garage.TurnOn(brightnessPct: 80, colorTempKelvin: 2700);
        }
        else
        {
            _logger.LogInformation("We are in a holiday light mode: {lightMode}", lightMode);

            _entities.Light.Garage1.TurnOn(
                brightnessPct: 80,
                rgbColor: holiday?.Hue?[colorPos % holiday.Hue.Count]);
            _entities.Light.Garage2.TurnOn(
                brightnessPct: 80,
                rgbColor: holiday?.Hue?[colorPos++ % holiday.Hue.Count]);
            _entities.Light.Garage3.TurnOn(
                brightnessPct: 80,
                rgbColor: holiday?.Hue?[colorPos++ % holiday.Hue.Count]);

            if (holiday?.TurnOnSwitches == true)
            {
                _entities.Switch.OutdoorPlug1.TurnOn();
                _entities.Switch.OutdoorPlug2.TurnOn();
            }

            IReadOnlyList<string> filteredEffectList = [.. _entities.Select.FrontPorchPreset!.Attributes!.Options!
                .Where(o => o.Contains(holiday?.Wled!))
                .OrderBy(_ => Random.Shared.NextDouble())];

            // Guard against empty effect list to avoid divide-by-zero when using modulo with Count
            if (filteredEffectList.Count > 0)
            {
                _entities.Select.FrontPorchPreset.SelectOption(option: filteredEffectList[wledEffectPos % filteredEffectList.Count]);
                _entities.Select.DeckPreset.SelectOption(option: filteredEffectList[wledEffectPos++ % filteredEffectList.Count]);
            }
            else
            {
                _logger.LogWarning("No matching WLED effects found for holiday {lightMode} (Wled='{WledName}'). Skipping WLED SelectOption calls.", lightMode, holiday?.Wled);
            }

            _scheduler.Schedule(TimeSpan.FromSeconds(30), repeat =>
            {
                if (lightOnPeriod.IsNow())
                {
                    _entities.Light.Garage1.TurnOn(
                        brightnessPct: 80,
                        rgbColor: holiday?.Hue?[colorPos++ % holiday.Hue.Count],
                        transition: 7);
                    _entities.Light.Garage2.TurnOn(
                        brightnessPct: 80,
                        rgbColor: holiday?.Hue?[colorPos++ % holiday.Hue.Count],
                        transition: 7);
                    _entities.Light.Garage3.TurnOn(
                        brightnessPct: 80,
                        rgbColor: holiday?.Hue?[colorPos++ % holiday.Hue.Count],
                        transition: 7);
                    repeat(TimeSpan.FromSeconds(30));
                }
            });

            // Only schedule WLED effect rotation if we have effects to rotate through
            if (filteredEffectList.Count > 0)
            {
                _scheduler.Schedule(TimeSpan.FromMinutes(10), repeat =>
                {
                    if (lightOnPeriod.IsNow())
                    {
                        _entities.Select.FrontPorchPreset.SelectOption(option: filteredEffectList[wledEffectPos % filteredEffectList.Count]);
                        _entities.Select.DeckPreset.SelectOption(option: filteredEffectList[wledEffectPos++ % filteredEffectList.Count]);
                        repeat(TimeSpan.FromMinutes(10));
                    }
                });
            }
        }

        var checkAttempts = 0;
        _scheduler.Schedule(TimeSpan.FromMinutes(10), repeat =>
        {
            var garage1 = _entities.Light.Garage1;
            var garage2 = _entities.Light.Garage2;
            var garage3 = _entities.Light.Garage3;
            var porch = _entities.Light.WledPorch;
            var deck = _entities.Light.WledDeck;
            var garageOn = garage1.IsOn() && garage2.IsOn() && garage3.IsOn();
            var garageBrightnessEqual = garage1.Attributes?.Brightness == garage2.Attributes?.Brightness && garage2.Attributes?.Brightness == garage3.Attributes?.Brightness;
            var porchOn = porch.IsOn();
            var deckOn = deck.IsOn();

            if ((garageOn && garageBrightnessEqual && /*porchOn &&*/ deckOn) || !lightOnPeriod.IsNow())
            {
                _logger.LogInformation("Lights on check successful.");
            }
            else
            {
                checkAttempts++;
                if (checkAttempts == 5)
                {
                    _logger.LogWarning("Light on check has failed 5 times.");
                    _telegram.System("Light on check has failed 5 times. There may be an issue.");
                }
                _entities.Light.Garage.TurnOn(brightnessPct: 80, colorTempKelvin: 2700);
                _entities.Light.WledPorch.TurnOn();
                _entities.Light.WledDeck.TurnOn();
                repeat(TimeSpan.FromMinutes(10));
            }
        });
    }

    private void TurnLightsOff()
    {
        var checkAttempts = 0;
        _logger.LogInformation("Turning off outside lights.");
        _entities.Light.WledDeck.TurnOff();
        _entities.Light.WledPorch.TurnOff();
        _entities.Light.Garage.TurnOff();
        _entities.Switch.OutdoorPlug1.TurnOff();
        _entities.Switch.OutdoorPlug2.TurnOff();

        _scheduler.Schedule(TimeSpan.FromMinutes(10), repeat =>
        {
            var garage1 = _entities.Light.Garage1;
            var garage2 = _entities.Light.Garage2;
            var garage3 = _entities.Light.Garage3;
            var porch = _entities.Light.WledPorch;
            var deck = _entities.Light.WledDeck;
            var garageOff = garage1.IsOff() && garage2.IsOff() && garage3.IsOff();
            var porchOff = porch.IsOff();
            var deckOff = deck.IsOff();

            if ((garageOff && /*porchOff &&*/ deckOff) || lightOnPeriod.IsNow())
            {
                _logger.LogInformation("Lights off check successful.");
            }
            else
            {
                checkAttempts++;
                if (checkAttempts == 5)
                {
                    _logger.LogWarning("Light off check has failed 5 times.");
                    _telegram.System("Light off check has failed 5 times. There may be an issue.");
                }
                _entities.Light.Garage.TurnOff();
                _entities.Light.WledPorch.TurnOff();
                _entities.Light.WledDeck.TurnOff();
                repeat(TimeSpan.FromMinutes(10));
            }
        });
    }
}


public class OutsideLightsConfig : Dictionary<string, HolidaySetting>
{
}

/// <summary>
/// Represents the lighting settings for a specific holiday.
/// </summary>
public class HolidaySetting
{
    /// <summary>
    /// A list of RGB color values for Hue lights. Each inner list represents a color.
    /// e.g., [[255, 0, 0], [0, 255, 0]] for red and green.
    /// </summary>
    public List<List<int>>? Hue { get; set; }

    /// <summary>
    /// The name of the WLED preset/effect to use.
    /// </summary>
    public string? Wled { get; set; }

    /// <summary>
    /// Optional flag to indicate if associated switches should be turned on.
    /// Defaults to false if not present in the YAML.
    /// </summary>
    public bool TurnOnSwitches { get; set; }

    /// <summary>
    /// The time period during which the holiday settings are active.
    /// </summary>
    public IPeriodTimeline? Period { get; set; }
}