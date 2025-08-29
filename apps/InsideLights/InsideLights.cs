using System.Collections.Generic;
using Occurify;
using Occurify.TimeZones;
using Occurify.Astro;
using Occurify.Extensions;
using Occurify.Reactive.Extensions;
using System.Reactive.Subjects;


namespace HomeAssistantApps;

[NetDaemonApp]
public class InsideLights
{
    private readonly BehaviorSubject<int> _colorTempSubject;
    protected int ColorTemp
    {
        get => _colorTempSubject.Value;
        private set => _colorTempSubject.OnNext(value);
    }
    private const int daytimeColorTemp = 5500;
    private const int nighttimeColorTemp = 2700;

    public InsideLights(ILogger<InsideLights> logger, IScheduler scheduler, LightEntities entities, ZoneEntities zoneEntities)
    {
        List<LightEntity> lights = [
            entities.AlexLamp,
            entities.JulieLamp,
            entities.LivingCanLights,
            entities.KitchenCanLights,
            entities.KitchenSink,
            entities.KitchenTable,
            entities.MarcySFunLampGroup,
            entities.MorganSFunLampGroup
        ];
        lights.ForEach((light) =>
        {
            light.StateChanges().Subscribe(s =>
            {
                if (s.New?.State == "on" && s.New?.Attributes?.ColorMode == "color_temp")
                {
                    light.TurnOn(colorTempKelvin: ColorTemp);
                }
            });
        });
        _colorTempSubject = new BehaviorSubject<int>(daytimeColorTemp);
        _colorTempSubject
            .DistinctUntilChanged()
            .Subscribe(newColorTemp =>
            {
                lights.ForEach((light) =>
                {
                    if (light.IsOn() && light.Attributes?.ColorMode == "color_temp")
                    {
                        light.TurnOn(colorTempKelvin: newColorTemp);
                    }
                });
            });
        ITimeline sixAm = TimeZoneInstants.DailyAt(hour: 6);
        ITimeline ninetyMinBeforeSunset = AstroInstants.LocalSunsets - TimeSpan.FromMinutes(90);
        ITimeline sixtyMinBeforeSunset = AstroInstants.LocalSunsets - TimeSpan.FromMinutes(60);
        IPeriodTimeline daytimePeriod = sixAm.To(ninetyMinBeforeSunset);
        IPeriodTimeline nighttimePeriod = sixtyMinBeforeSunset.To(sixAm);

        if (!daytimePeriod.IsNow() && !nighttimePeriod.IsNow())
        {
            ColorTemp = nighttimeColorTemp;
        }

        daytimePeriod.SubscribeStartEnd(
            () => ColorTemp = daytimeColorTemp,
            () =>
            {
                scheduler.Schedule(TimeSpan.Zero, repeat =>
                {
                    if (ColorTemp > nighttimeColorTemp)
                    {
                        ColorTemp = Math.Max(nighttimeColorTemp, ColorTemp - (daytimeColorTemp - nighttimeColorTemp) / 10);
                        repeat(TimeSpan.FromMinutes(3));
                    }
                });
            },
            Scheduler.Default);

        nighttimePeriod.SubscribeStart(() => ColorTemp = nighttimeColorTemp, Scheduler.Default);
    }
}