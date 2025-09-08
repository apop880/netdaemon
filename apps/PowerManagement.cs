namespace HomeAssistantApps;

[NetDaemonApp]
public class PowerManagement
{
    private const double LowBatteryWarningThreshold = 80.0;
    private const double CriticalBatteryShutdownThreshold = 60.0;
    private const double FullyChargedThreshold = 100.0;

    public PowerManagement(ILogger<PowerManagement> logger, Entities entities, Telegram telegram, Server server)
    {
        var shutdownSwitch = entities.InputBoolean.ShutdownOnUps;
        var upsCharge = entities.Sensor.ServerUpsBatteryCharge;
        var upsStatus = entities.Sensor.ServerUpsStatus;
        var shutdownButton = entities.InputButton.ShutdownServer;

        shutdownButton.StateChanges()
            .SubscribeAsync(async _ =>
            {
                logger.LogInformation("Shutdown button pressed. Initiating shutdown process.");
                telegram.All("The home server is shutting down now for maintenance.");
                await server.Shutdown();
            });

        upsStatus.StateChanges()
            .Where(s => s.New?.State == "On Battery" || s.New?.State == "Online")
            .Subscribe(s =>
            {
                if (s.New?.State == "On Battery")
                {
                    telegram.All("Power has been lost to UPS. Currently discharging.");
                }
                else if (s.New?.State == "Online")
                {
                    telegram.All("Power has been restored to UPS. Currently charging.");
                }                
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
                if (newState == FullyChargedThreshold && oldState < FullyChargedThreshold && shutdownSwitch.IsOff())
                {
                    telegram.All("UPS is fully charged.");
                    shutdownSwitch.TurnOn();
                }
            });
    }
}