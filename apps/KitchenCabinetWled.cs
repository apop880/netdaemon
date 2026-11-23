using System.Net.Http;
using System.Text.Json.Nodes;
using NetDaemon.Extensions.Scheduler;

namespace HomeAssistantApps;

[NetDaemonApp]
public class KitchenCabinetWled
{
    internal sealed record WledPreset(
        string Name,
        int EffectId,
        int PaletteId,
        int Speed,
        int Intensity,
        int Brightness = 180,
        int[][]? Colors = null,
        int? Custom1 = null,
        int? Custom2 = null,
        int? Custom3 = null,
        bool? Option1 = null);

    private readonly ILogger<KitchenCabinetWled> _logger;
    private readonly IScheduler _scheduler;
    private readonly Entities _entities;
    private readonly HolidayCalendar _holidays;
    private readonly string _wledIp;
    private readonly HttpClient _httpClient;

    private IDisposable? _effectCycleSubscription;
    private int _currentEffectIndex;
    // Verified against live WLED 0.15.3 (250 RGB LEDs, 1D strip):
    // Effects: 102=Candle Multi, 67=Colorwaves, 107=Noise Pal, 80=Twinklefox,
    //          115=Blends, 110=Flow, 101=Pacifica
    // Palettes: 0=Default, 4=* Color Gradient, 13=Sunset, 21=Sunset 2, 47=Orangery, 51=Atlantica
    internal static readonly WledPreset[] EveningPresets =
    [
        // Warm amber & honey tea-light flicker — uses primary/secondary colors (col[0]/col[1]) with pal=0
        new("Amber Candlelight", EffectId: 102, PaletteId: 0, Speed: 75, Intensity: 175, Brightness: 175,
            Colors: [[255, 138, 28], [40, 12, 0]]),

        // Custom warm kitchen gradient: candlelight -> amber -> deep ember, gently drifting
        new("Warm Hearth Drift", EffectId: 67, PaletteId: 4, Speed: 32, Intensity: 110, Brightness: 185,
            Colors: [[255, 168, 76], [230, 82, 12], [110, 22, 4]]),

        // Slow organic Perlin-style waves in rich burnt orange / copper / warm gold
        new("Copper Ember", EffectId: 107, PaletteId: 47, Speed: 36, Intensity: 96, Brightness: 175),

        // Soft warm-white & golden fairy shimmer over a dim ember glow (o1=false keeps warm palette)
        new("Champagne Twinkle", EffectId: 80, PaletteId: 4, Speed: 38, Intensity: 140, Brightness: 180,
            Colors: [[255, 200, 125], [220, 115, 28], [36, 12, 2]], Option1: false),

        // Silky multi-zone warm sunset flow (amber, coral, muted plum) without harsh black dropouts
        new("Golden Hour Flow", EffectId: 110, PaletteId: 13, Speed: 28, Intensity: 80, Brightness: 180),

        // Ultra-slow velvety color morphs through warm bronze, amber, and terracotta
        new("Velvet Sunset", EffectId: 115, PaletteId: 21, Speed: 24, Intensity: 72, Brightness: 175),

        // Subtle warm-rose & candlelight glow for late-evening dining vibe
        new("Rosé Glow", EffectId: 67, PaletteId: 4, Speed: 26, Intensity: 95, Brightness: 175,
            Colors: [[255, 182, 120], [225, 72, 68], [96, 20, 48]]),

        // Calm, low-key oceanic shimmer (one cool-tone breather in the rotation)
        new("Moonlit Pacifica", EffectId: 101, PaletteId: 51, Speed: 42, Intensity: 120, Brightness: 155),
    ];

    // Verified against live WLED 0.15.3 (250 RGB LEDs, 1D strip):
    // Effects: 80=Twinklefox, 67=Colorwaves, 102=Candle Multi, 107=Noise Pal,
    //          110=Flow, 115=Blends, 103=Solid Glitter, 74=Colortwinkles
    // Palettes: 0=Default, 4=* Color Gradient, 5=* Colors Only, 10=Forest, 48=C9, 52=C9 2
    internal static readonly WledPreset[] ChristmasPresets =
    [
        // Classic vintage C9 bulb twinkle (red, gold, green, blue) over a warm dim base
        new("Vintage C9 Twinkle", EffectId: 80, PaletteId: 48, Speed: 42, Intensity: 150, Brightness: 185,
            Option1: false),

        // Soft red, evergreen & warm gold drifting waves
        new("Holly & Gold Drift", EffectId: 67, PaletteId: 4, Speed: 30, Intensity: 105, Brightness: 185,
            Colors: [[220, 18, 18], [16, 130, 32], [255, 190, 75]]),

        // Warm crimson hearth candlelight flicker
        new("Fireside Crimson Candle", EffectId: 102, PaletteId: 0, Speed: 70, Intensity: 175, Brightness: 175,
            Colors: [[235, 36, 16], [52, 8, 2]]),

        // Deep evergreen base with delicate warm-white fairy-light sparkle
        new("Evergreen Fairy Lights", EffectId: 103, PaletteId: 0, Speed: 48, Intensity: 90, Brightness: 180,
            Colors: [[12, 92, 26], [0, 0, 0], [255, 224, 155]]),

        // Classic candy-cane peppermint stripes gently flowing
        new("Peppermint Flow", EffectId: 110, PaletteId: 4, Speed: 28, Intensity: 85, Brightness: 180,
            Colors: [[235, 16, 20], [255, 232, 210], [180, 12, 18]]),

        // Gentle multi-color C9 holiday shimmer with warm amber accents
        new("C9 Holiday Shimmer", EffectId: 74, PaletteId: 52, Speed: 48, Intensity: 135, Brightness: 185),

        // Slow organic Perlin waves across deep pine and emerald forest tones
        new("Winter Pine Glow", EffectId: 107, PaletteId: 10, Speed: 32, Intensity: 96, Brightness: 170),

        // Ultra-slow velvety blend through cranberry red, warm champagne gold, and pine green
        new("Velvet Noel", EffectId: 115, PaletteId: 5, Speed: 24, Intensity: 78, Brightness: 180,
            Colors: [[210, 18, 28], [255, 198, 95], [14, 118, 34]]),
    ];

    private const int EveningStartHour = 17;
    private const int NightModeStartHour = 23;
    private const int NightModeBrightness = 38;
    private const int EffectCycleIntervalMinutes = 12;
    private const int TransitionDuration = 25; // 2.5 seconds (WLED transition is in 100ms units)
    public KitchenCabinetWled(
        ILogger<KitchenCabinetWled> logger,
        IScheduler scheduler,
        Entities entities,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        HolidayCalendar holidays)
    {
        _logger = logger;
        _scheduler = scheduler;
        _entities = entities;
        _holidays = holidays;
        _wledIp = configuration.GetValue<string>("Wled:KitchenCabinetsIp") ?? "192.168.1.94";
        _httpClient = httpClientFactory.CreateClient();
        _logger.LogInformation("KitchenCabinetWled: Initializing with WLED IP {Ip}", _wledIp);

        ScheduleEveningStart();
        ScheduleNightModeStart();
        SubscribeToNightModeChanges();
        SubscribeToHolidayChanges();

        if (IsCurrentlyEvening() && _entities.InputBoolean.NightMode.IsOff())
        {
            _logger.LogInformation("KitchenCabinetWled: Currently evening, starting evening mode");
            StartEveningMode();
        }
        else if (_entities.InputBoolean.NightMode.IsOn())
        {
            _logger.LogInformation("KitchenCabinetWled: Night mode already on, starting night mode");
            StartNightMode();
        }
    }

    private bool IsCurrentlyEvening()
    {
        var now = _scheduler.Now.LocalDateTime;
        var hour = now.Hour;
        return hour >= EveningStartHour && hour < NightModeStartHour;
    }

    private void ScheduleEveningStart()
    {
        _scheduler.ScheduleCron($"0 {EveningStartHour} * * *", () =>
        {
            if (_entities.InputBoolean.NightMode.IsOff())
            {
                _logger.LogInformation("KitchenCabinetWled: Evening scheduled start at {Hour}:00", EveningStartHour);
                StartEveningMode();
            }
        });
    }

    private void ScheduleNightModeStart()
    {
        _scheduler.ScheduleCron($"0 {NightModeStartHour} * * *", () =>
        {
            _logger.LogInformation("KitchenCabinetWled: Night mode scheduled start at {Hour}:00", NightModeStartHour);
            StartNightMode();
        });
    }

    private void SubscribeToNightModeChanges()
    {
        _entities.InputBoolean.NightMode
            .StateChanges()
            .Subscribe(s =>
            {
                if (s.New?.State == "on")
                {
                    _logger.LogInformation("KitchenCabinetWled: Night mode turned on");
                    StartNightMode();
                }
                else if (s.New?.State == "off")
                {
                    _logger.LogInformation("KitchenCabinetWled: Night mode turned off");
                    if (IsCurrentlyEvening())
                    {
                        StartEveningMode();
                    }
                    else
                    {
                        TurnOffLight();
                    }
                }
            });
    }

    private void SubscribeToHolidayChanges()
    {
        _holidays.Subscribe(
            Holiday.Christmas,
            onStart: () =>
            {
                _logger.LogInformation("KitchenCabinetWled: Christmas season started");
                if (IsCurrentlyEvening() && _entities.InputBoolean.NightMode.IsOff())
                {
                    StartEveningMode();
                }
            },
            onEnd: () =>
            {
                _logger.LogInformation("KitchenCabinetWled: Christmas season ended");
                if (IsCurrentlyEvening() && _entities.InputBoolean.NightMode.IsOff())
                {
                    StartEveningMode();
                }
            },
            _scheduler);
    }

    internal WledPreset[] GetActiveEveningPresets() =>
        SelectEveningPresets(_holidays, _scheduler.Now.UtcDateTime);

    internal static WledPreset[] SelectEveningPresets(HolidayCalendar holidays, DateTime utcInstant) =>
        holidays.IsActive(Holiday.Christmas, utcInstant)
            ? ChristmasPresets
            : EveningPresets;

    private void StartEveningMode()
    {
        _effectCycleSubscription?.Dispose();
        _effectCycleSubscription = null;

        var initialPresets = GetActiveEveningPresets();
        _currentEffectIndex = Random.Shared.Next(initialPresets.Length);
        ApplyEveningEffect();

        _effectCycleSubscription = _scheduler.Schedule(
            TimeSpan.FromMinutes(EffectCycleIntervalMinutes),
            repeat =>
            {
                if (_entities.InputBoolean.NightMode.IsOn() || !IsCurrentlyEvening())
                {
                    _logger.LogDebug("KitchenCabinetWled: Stopping effect cycle - night mode on or outside evening hours");
                    return;
                }

                var activePresets = GetActiveEveningPresets();
                _currentEffectIndex = (_currentEffectIndex + 1) % activePresets.Length;
                ApplyEveningEffect();
                repeat(TimeSpan.FromMinutes(EffectCycleIntervalMinutes));
            });

        var modeLabel = _holidays.IsActive(Holiday.Christmas, _scheduler.Now.UtcDateTime) ? "Christmas" : "Standard";
        _logger.LogInformation("KitchenCabinetWled: Evening mode ({Mode}) started with effect cycling every {Min} minutes",
            modeLabel, EffectCycleIntervalMinutes);
    }

    private void ApplyEveningEffect()
    {
        var presets = GetActiveEveningPresets();
        var preset = presets[_currentEffectIndex % presets.Length];
        var isChristmas = ReferenceEquals(presets, ChristmasPresets);
        _logger.LogInformation("KitchenCabinetWled: Applying {Season} preset: {Name} (fx={Fx}, pal={Pal})",
            isChristmas ? "Christmas" : "evening", preset.Name, preset.EffectId, preset.PaletteId);

        SendWledState(BuildPresetState(preset));
    }

    internal static JsonObject BuildPresetState(WledPreset preset)
    {
        var seg = new JsonObject
        {
            ["id"] = 0,
            ["fx"] = preset.EffectId,
            ["pal"] = preset.PaletteId,
            ["sx"] = preset.Speed,
            ["ix"] = preset.Intensity,
            ["c1"] = preset.Custom1 ?? 128,
            ["c2"] = preset.Custom2 ?? 128,
            ["c3"] = preset.Custom3 ?? 16,
            ["o1"] = preset.Option1 ?? false,
            ["o2"] = false,
            ["o3"] = false
        };

        if (preset.Colors is { Length: > 0 })
        {
            var col = new JsonArray();
            foreach (var rgb in preset.Colors)
            {
                col.Add(new JsonArray { rgb[0], rgb[1], rgb[2] });
            }
            seg["col"] = col;
        }

        return new JsonObject
        {
            ["on"] = true,
            ["bri"] = preset.Brightness,
            ["transition"] = TransitionDuration,
            ["seg"] = new JsonArray { seg }
        };
    }

    private void StartNightMode()
    {
        _effectCycleSubscription?.Dispose();
        _effectCycleSubscription = null;

        _logger.LogInformation("KitchenCabinetWled: Starting night mode - 15% brightness, warm white");

        var state = new JsonObject
        {
            ["on"] = true,
            ["bri"] = NightModeBrightness,
            ["transition"] = TransitionDuration,
            ["seg"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 0,
                    ["fx"] = 0,
                    ["pal"] = 0,
                    ["col"] = new JsonArray
                    {
                        new JsonArray { 255, 196, 130 } // Soothing ~2700K warm white on RGB strip
                    }
                }
            }
        };

        SendWledState(state);
    }

    private void TurnOffLight()
    {
        _effectCycleSubscription?.Dispose();
        _effectCycleSubscription = null;

        _logger.LogInformation("KitchenCabinetWled: Turning off light");

        var state = new JsonObject
        {
            ["on"] = false,
            ["transition"] = TransitionDuration
        };

        SendWledState(state);
    }

    private async void SendWledState(JsonNode state)
    {
        try
        {
            var json = state.ToJsonString();
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"http://{_wledIp}/json/state", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("KitchenCabinetWled: WLED API returned {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KitchenCabinetWled: Failed to send WLED state");
        }
    }
}