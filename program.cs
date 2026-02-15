using System.Reflection;
using Microsoft.Extensions.Hosting;
using NetDaemon.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.Extensions.Tts;
using NetDaemon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Occurify.Astro;
using System.Net.Http;

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
                .AddTransient<Proxmox>();
            services.AddHttpClient("IgnoreSslClient")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (request, cert, chain, sslPolicyErrors) => true
                });
        }
        )
        .Build();
    var configuration = host.Services.GetRequiredService<IConfiguration>();
    var latitude = configuration.GetValue<double>("Coordinates:Latitude", 78.2384); // Fallback if not found
    var longitude = configuration.GetValue<double>("Coordinates:Longitude", 15.4463);
    var height = configuration.GetValue<double>("Coordinates:Elevation", 126);

    Coordinates.Local = new Coordinates(latitude, longitude, height);
    
    await host
        .RunAsync()
        .ConfigureAwait(false);
}
catch (Exception e)
{
    Console.WriteLine($"Failed to start host... {e}");
    throw;
}