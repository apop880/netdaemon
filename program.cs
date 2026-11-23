using System.Reflection;
using Microsoft.Extensions.Hosting;
using NetDaemon.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.Extensions.Tts;
using NetDaemon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Occurify.Astro;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

#pragma warning disable CA1812

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseNetDaemonAppSettings()
        .UseNetDaemonDefaultLogging()
        .UseNetDaemonRuntime()
        .UseNetDaemonTextToSpeech()
        .ConfigureServices((hostContext, services) =>
        {
            services.Configure<TelegramSettings>(hostContext.Configuration.GetSection("Telegram"));
            services
                .AddAppsFromAssembly(Assembly.GetExecutingAssembly())
                .AddNetDaemonStateManager()
                .AddNetDaemonScheduler()
                .AddHomeAssistantGenerated()
                .AddHttpClient()
                .AddSingleton<Telegram>()
                .AddTransient<Notify>()
                .AddScoped<HomeMode>()
                .AddSingleton<Server>()
                .AddTransient<Unifi>()
                .AddTransient<Proxmox>()
                .AddSingleton<HolidayCalendar>();
            services.AddHttpClient("IgnoreSslClient")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (request, cert, chain, sslPolicyErrors) => true
                });
        }
        )
        .Build();
    var configuration = host.Services.GetRequiredService<IConfiguration>();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    Coordinates.Local = await GetHomeAssistantCoordinatesAsync(host.Services, configuration, logger).ConfigureAwait(false);
    
    await host
        .RunAsync()
        .ConfigureAwait(false);
}
catch (Exception e)
{
    Console.WriteLine($"Failed to start host... {e}");
    throw;
}

static async Task<Coordinates> GetHomeAssistantCoordinatesAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
{
    var fallbackLat = configuration.GetValue<double>("Coordinates:Latitude", 78.2384);
    var fallbackLon = configuration.GetValue<double>("Coordinates:Longitude", 15.4463);
    var fallbackElevation = configuration.GetValue<double>("Coordinates:Elevation", 126);

    var host = configuration.GetValue<string>("HomeAssistant:Host");
    var port = configuration.GetValue<int>("HomeAssistant:Port", 8123);
    var ssl = configuration.GetValue<bool>("HomeAssistant:Ssl", false);
    var token = configuration.GetValue<string>("HomeAssistant:Token");

    if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
    {
        logger.LogWarning("HomeAssistant Host or Token missing; using fallback coordinates.");
        return new Coordinates(fallbackLat, fallbackLon, fallbackElevation);
    }

    try
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        using var client = httpClientFactory.CreateClient(ssl ? "IgnoreSslClient" : string.Empty);
        client.Timeout = TimeSpan.FromSeconds(10);
        var scheme = ssl ? "https" : "http";
        var apiPrefix = host.Equals("supervisor", StringComparison.OrdinalIgnoreCase) ? "/core/api" : "/api";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{scheme}://{host}:{port}{apiPrefix}/config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        var lat = json?["latitude"]?.GetValue<double>();
        var lon = json?["longitude"]?.GetValue<double>();
        var elevation = json?["elevation"]?.GetValue<double>() ?? fallbackElevation;

        if (lat.HasValue && lon.HasValue)
        {
            logger.LogInformation("Loaded coordinates from Home Assistant ({Latitude}, {Longitude}, {Elevation}).", lat.Value, lon.Value, elevation);
            return new Coordinates(lat.Value, lon.Value, elevation);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to fetch coordinates from Home Assistant /api/config; using fallback coordinates.");
    }

    return new Coordinates(fallbackLat, fallbackLon, fallbackElevation);
}