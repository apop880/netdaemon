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
    private readonly HolidayCalendar _holidays;
    private readonly IScheduler _scheduler;
    private readonly Notify _notify;
    private readonly IPeriodTimeline lightOnPeriod;
    private readonly bool _controlGarageLights = false;


    public OutsideLightsApp(
        ILogger<OutsideLightsApp> logger,
        IScheduler scheduler,
        Entities entities,
        Notify notify,
        HolidayCalendar holidays)
    {
        _logger = logger;
        _entities = entities;
        _scheduler = scheduler;
        _notify = notify;
        _holidays = holidays;

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
        var holiday = _holidays.GetActiveHoliday();
        _entities.Light.WledDeck.TurnOn();

        if (holiday is null)
        {
            if (_controlGarageLights)
            {
                _entities.Light.Garage.TurnOn(brightnessPct: 80, colorTempKelvin: 2700);
            }
        }
        else
        {
            _logger.LogInformation("We are in a holiday light mode: {lightMode}", holiday.Name);

            if (_controlGarageLights && holiday.HueColors is { Count: > 0 } hueColors)
            {
                _entities.Light.Garage1.TurnOn(
                    brightnessPct: 80,
                    rgbColor: hueColors[colorPos % hueColors.Count]);
                _entities.Light.Garage2.TurnOn(
                    brightnessPct: 80,
                    rgbColor: hueColors[colorPos++ % hueColors.Count]);
                _entities.Light.Garage3.TurnOn(
                    brightnessPct: 80,
                    rgbColor: hueColors[colorPos++ % hueColors.Count]);
            }

            if (holiday.TurnOnOutdoorSwitches)
            {
                _entities.Switch.OutdoorPlug1.TurnOn();
                _entities.Switch.OutdoorPlug2.TurnOn();
            }

            IReadOnlyList<string> filteredEffectList = holiday.WledPresetKeyword is { Length: > 0 } keyword
                ? [.. _entities.Select.DeckPreset!.Attributes!.Options!
                    .Where(o => o.Contains(keyword))
                    .OrderBy(_ => Random.Shared.NextDouble())]
                : [];

            // Guard against empty effect list to avoid divide-by-zero when using modulo with Count
            if (filteredEffectList.Count > 0)
            {
                _entities.Select.DeckPreset.SelectOption(option: filteredEffectList[wledEffectPos++ % filteredEffectList.Count]);
            }
            else
            {
                _logger.LogWarning("No matching WLED effects found for holiday {lightMode} (Wled='{WledName}'). Skipping WLED SelectOption calls.", holiday.Name, holiday.WledPresetKeyword);
            }

            if (_controlGarageLights && holiday.HueColors is { Count: > 0 } rotatingColors)
            {
                _scheduler.Schedule(TimeSpan.FromSeconds(30), repeat =>
                {
                    if (lightOnPeriod.IsNow())
                    {
                        _entities.Light.Garage1.TurnOn(
                            brightnessPct: 80,
                            rgbColor: rotatingColors[colorPos++ % rotatingColors.Count],
                            transition: 7);
                        _entities.Light.Garage2.TurnOn(
                            brightnessPct: 80,
                            rgbColor: rotatingColors[colorPos++ % rotatingColors.Count],
                            transition: 7);
                        _entities.Light.Garage3.TurnOn(
                            brightnessPct: 80,
                            rgbColor: rotatingColors[colorPos++ % rotatingColors.Count],
                            transition: 7);
                        repeat(TimeSpan.FromSeconds(30));
                    }
                });
            }

            // Only schedule WLED effect rotation if we have effects to rotate through
            if (filteredEffectList.Count > 0)
            {
                _scheduler.Schedule(TimeSpan.FromMinutes(10), repeat =>
                {
                    if (lightOnPeriod.IsNow())
                    {
                        _entities.Select.DeckPreset.SelectOption(option: filteredEffectList[wledEffectPos++ % filteredEffectList.Count]);
                        repeat(TimeSpan.FromMinutes(10));
                    }
                });
            }
        }

        var checkAttempts = 0;
        _scheduler.Schedule(TimeSpan.FromMinutes(10), repeat =>
        {
            var deck = _entities.Light.WledDeck;
            var deckOn = deck.IsOn();
            bool lightsOnGood;
            if (_controlGarageLights)
            {
                var garage1 = _entities.Light.Garage1;
                var garage2 = _entities.Light.Garage2;
                var garage3 = _entities.Light.Garage3;
                var garageOn = garage1.IsOn() && garage2.IsOn() && garage3.IsOn();
                var garageBrightnessEqual = garage1.Attributes?.Brightness == garage2.Attributes?.Brightness && garage2.Attributes?.Brightness == garage3.Attributes?.Brightness;
                lightsOnGood = garageOn && garageBrightnessEqual && deckOn;
            }
            else
            {
                lightsOnGood = deckOn;
            }

            if (lightsOnGood || !lightOnPeriod.IsNow())
            {
                _logger.LogInformation("Lights on check successful.");
            }
            else
            {
                checkAttempts++;
                if (checkAttempts == 5)
                {
                    _logger.LogWarning("Light on check has failed 5 times.");
                    _notify.Alex("Light on check has failed 5 times. There may be an issue.");
                }
                if (_controlGarageLights)
                {
                    _entities.Light.Garage.TurnOn(brightnessPct: 80, colorTempKelvin: 2700);
                }
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
        if (_controlGarageLights)
        {
            _entities.Light.Garage.TurnOff();
        }
        _entities.Switch.OutdoorPlug1.TurnOff();
        _entities.Switch.OutdoorPlug2.TurnOff();

        _scheduler.Schedule(TimeSpan.FromMinutes(10), repeat =>
        {
            var deck = _entities.Light.WledDeck;
            var deckOff = deck.IsOff();
            bool lightsOffGood;
            if (_controlGarageLights)
            {
                var garage1 = _entities.Light.Garage1;
                var garage2 = _entities.Light.Garage2;
                var garage3 = _entities.Light.Garage3;
                var garageOff = garage1.IsOff() && garage2.IsOff() && garage3.IsOff();
                lightsOffGood = garageOff && deckOff;
            }
            else
            {
                lightsOffGood = deckOff;
            }

            if (lightsOffGood || lightOnPeriod.IsNow())
            {
                _logger.LogInformation("Lights off check successful.");
            }
            else
            {
                checkAttempts++;
                if (checkAttempts == 5)
                {
                    _logger.LogWarning("Light off check has failed 5 times.");
                    _notify.Alex("Light off check has failed 5 times. There may be an issue.");
                }
                if (_controlGarageLights)
                {
                    _entities.Light.Garage.TurnOff();
                }
                _entities.Light.WledDeck.TurnOff();
                repeat(TimeSpan.FromMinutes(10));
            }
        });
    }
}
