using System.Linq;
using System.Collections.Generic;
using NetDaemon.Extensions.Scheduler;

namespace HomeAssistantApps;

[NetDaemonApp]
public class ChristmasTree
{
    public ChristmasTree(Entities entities, IScheduler scheduler, IConfiguration configuration)
    {
        var effectIdx = 0;
        var tree = entities.Light.ChristmasTree;
        var topper = entities.Switch.MorganTabletSmartPlug; //To-do: change entity name in HA
        var playlist = entities.InputBoolean.ChristmasTreePlaylist;
        var tokenUserId = configuration.GetValue<string>("HomeAssistant:TokenUserId");
        List<string> effects = tree.Attributes?.EffectList?.OrderBy(_ => Random.Shared.NextDouble()).ToList() ?? [];
        scheduler.ScheduleCron("0 0 * * *", () =>
        {
            effectIdx = 0;
            // randomize the effects list
            effects = [.. effects.OrderBy(_ => Random.Shared.NextDouble())];
        });

        scheduler.RunEvery(TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow, () =>
        {
            if (tree.State != "on" || playlist.State != "on" || effects.Count == 0)
                return;

            effectIdx = (effectIdx + 1) % effects.Count;
            tree.TurnOn(effect: effects[effectIdx]);
        });

        tree.StateAllChanges()
            .Subscribe(e =>
            {
                var newSet = new HashSet<string>(e.New?.Attributes?.EffectList ?? Enumerable.Empty<string>());
                var oldSet = new HashSet<string>(e.Old?.Attributes?.EffectList ?? Enumerable.Empty<string>());
                if (!newSet.SetEquals(oldSet))
                {
                    // Effect list has changed, re-randomize
                    effects = newSet.OrderBy(_ => Guid.NewGuid()).ToList() ?? [];
                    effectIdx = 0;
                }

                else if (!string.IsNullOrEmpty(tokenUserId) && e.New?.Context?.UserId != tokenUserId && e.New?.Attributes?.Effect != e.Old?.Attributes?.Effect)
                {
                    // User changed effect manually, turn off playlist
                    playlist.TurnOff();
                }

                // Sync topper with tree
                else if (e.New?.State == "on" && e.Old?.State == "off")
                {
                    topper.TurnOn();
                }

                else if (e.New?.State == "off" && e.Old?.State == "on")
                {
                    topper.TurnOff();
                }
            });
    }
}