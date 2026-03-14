using NetDaemon.Extensions.Scheduler;

namespace HomeAssistantApps;

[NetDaemonApp]
public class OfficeLampColorCycle
{
    public OfficeLampColorCycle(ILogger<OfficeLampColorCycle> logger, IScheduler scheduler, Entities entities)
    {
        scheduler.RunEvery(TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow, () =>
        {
            var lamp = entities.Light.OfficeLamp;
            if (lamp.State != "on")
                return;

            var (r, g, b) = GeneratePleasantColor();
            lamp.TurnOn(rgbColor: [r, g, b], transition: 5);
            logger.LogDebug("Office lamp color changed to RGB({R}, {G}, {B})", r, g, b);
        });
    }

    private static (int R, int G, int B) GeneratePleasantColor()
    {
        int r, g, b;

        do
        {
            r = Random.Shared.Next(50, 221);
            g = Random.Shared.Next(50, 221);
            b = Random.Shared.Next(50, 221);
        }
        while (IsHarshColor(r, g, b));

        return (r, g, b);
    }

    private static bool IsHarshColor(int r, int g, int b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var saturation = max == 0 ? 0 : (max - min) / (double)max;

        if (saturation < 0.15)
            return true;

        if (r > 200 && g > 200 && b > 200)
            return true;

        if (r > 200 && g < 80 && b < 80)
            return true;
        if (g > 200 && r < 80 && b < 80)
            return true;
        if (b > 200 && r < 80 && g < 80)
            return true;

        return false;
    }
}
