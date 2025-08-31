namespace HomeAssistantApps;

[NetDaemonApp]
public class Ups
{
    private const double LowBatteryWarningThreshold = 80.0;
    private const double CriticalBatteryShutdownThreshold = 60.0;
    private const double FullyChargedThreshold = 100.0;

    public Ups(ILogger<Ups> logger, Entities entities, Telegram telegram, Server server)
    {
        var shutdownSwitch = entities.InputBoolean.ShutdownOnUps;
        var upsCharge = entities.Sensor.ServerUpsBatteryCharge;
        var upsStatus = entities.Sensor.ServerUpsStatus;

        upsStatus.StateChanges()
            .Where(s => s.New?.State != s.Old?.State && s.New?.State == "On Battery")
            .Subscribe(_ =>
            {
                // Power lost (discharging from 100%)
                telegram.All("Power has been lost to UPS. Currently discharging.");
            });

        upsCharge.StateChanges()
            .Where(s => s.New?.State is not null && s.Old?.State is not null)
            .SubscribeAsync(async s =>
            {
                var newState = s.New!.State!.Value;
                var oldState = s.Old!.State!.Value;

                // Crossed below 80%
                if (newState < LowBatteryWarningThreshold && oldState >= LowBatteryWarningThreshold)
                {
                    var shutdownStatus = shutdownSwitch.IsOn() ? "on" : "off";
                    telegram.All($"UPS battery has dropped below {LowBatteryWarningThreshold}%. Home server will initiate shutdown at {CriticalBatteryShutdownThreshold}% if shutdown switch is on. Shutdown switch is currently {shutdownStatus}.");
                }

                // Crossed below 60%
                if (newState < CriticalBatteryShutdownThreshold && oldState >= CriticalBatteryShutdownThreshold)
                {
                    if (shutdownSwitch.IsOn())
                    {
                        telegram.All($"UPS battery has dropped below {CriticalBatteryShutdownThreshold}%. Initiating shutdown process.");
                        await server.Shutdown();
                    }
                    else
                    {
                        telegram.All($"UPS battery has dropped below {CriticalBatteryShutdownThreshold}%. Shutdown switch is off.");
                    }
                }

                // Reached 100%
                if (newState == FullyChargedThreshold && oldState < FullyChargedThreshold)
                {
                    telegram.All("UPS is fully charged.");
                    shutdownSwitch.TurnOn();
                }
            });
    }
}