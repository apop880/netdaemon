# NetDaemon Project - Agent Guidelines

## Project Overview

This is a NetDaemon application for Home Assistant automation using C# and .NET 10.0. The project automates smart home functionality including lights, notifications, media players, and integrations with external services (Proxmox, Unifi, Telegram).

## Build, Lint, and Test Commands

### Build
```bash
dotnet build
```

### Clean
```bash
dotnet clean
```

### Deploy
```bash
./deploy.sh
```

### Restore Dependencies
```bash
dotnet restore
```

### Run
```bash
dotnet run
```

### Lint/Format
```bash
dotnet format --verify-no-changes
dotnet format
```

### Tests
No test framework is currently configured. If tests are added:
```bash
dotnet test                           # Run all tests
dotnet test --filter "FullyQualifiedName~TestClassName"  # Run specific test class
dotnet test --filter "FullyQualifiedName~TestMethodName" # Run single test
```

## Project Structure

```
netdaemon/
├── program.cs              # Application entry point, DI configuration
├── apps/                    # NetDaemon automation apps
│   ├── GlobalUsings.cs      # Global using statements
│   ├── InsideLights/        # Subfolder apps (allowed)
│   ├── OutsideLights/
│   └── NetlifyDNS/
├── modules/                 # Shared services/helpers
├── extensions/              # Extension methods
├── HomeAssistantGenerated.cs # Auto-generated entities
└── appsettings.json         # Configuration
```

## Code Style Guidelines

### Language Features
- Target: .NET 10.0, C# 14.0
- Nullable reference types enabled - always handle nulls explicitly
- Use collection expressions: `List<string> items = ["a", "b"];`
- Primary constructors for simple classes: `public class Notify(IHaContext haContext)`
- Required properties for config classes: `public required string Name { get; set; }`

### Imports and Namespaces
- Global usings defined in `apps/GlobalUsings.cs` - do not duplicate these imports
- Place additional using statements at the top of the file
- Common global usings already available:
  - `System`, `System.Reactive.Linq`, `System.Reactive.Concurrency`
  - `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Configuration`
  - `NetDaemon.AppModel`, `NetDaemon.HassModel`, `NetDaemon.HassModel.Entities`
  - `HomeAssistantGenerated`, `HomeAssistantApps.modules`

### Naming Conventions
- **Classes**: PascalCase (e.g., `Bedtime`, `KidsWakeup`, `TrashMonitor`)
- **Private fields**: Underscore prefix (e.g., `_logger`, `_services`, `_config`)
- **Properties**: PascalCase (e.g., `Entity`, `LinkedMediaPlayer`)
- **Local variables**: camelCase (e.g., `currentStatus`, `isRecycling`)
- **Config classes**: Suffix with `Config` (e.g., `BedtimeConfig`, `ZoozConfig`)
- **Methods**: PascalCase (e.g., `CheckTrashSchedule`, `GetHolidays`)

### NetDaemon App Pattern
```csharp
[NetDaemonApp]
public class MyApp
{
    public MyApp(ILogger<MyApp> logger, Entities entities, Services services)
    {
        // Subscribe to entity state changes
        entities.Light.MyLight
            .StateChanges()
            .Subscribe(s => 
            {
                if (s.New?.State == "on")
                {
                    logger.LogInformation("Light turned on");
                }
            });
    }
}
```

### Dependency Injection
- Constructor injection is the standard pattern
- Register services in `program.cs`:
  - `services.AddSingleton<T>()` for shared state
  - `services.AddScoped<T>()` for per-request
  - `services.AddTransient<T>()` for lightweight services
- Common injected types: `ILogger<T>`, `Entities`, `Services`, `IHaContext`, `IScheduler`, `IConfiguration`

### Reactive Programming
- Use `StateChanges()` for entity state monitoring
- Use `Subscribe()` or `SubscribeAsync()` for event handling
- Chain operators: `.Where()`, `.Select()`, `.DistinctUntilChanged()`
- Use `WhenStateIsFor()` for debounced state checks
- Dispose subscriptions with `IDisposable?` fields when needed

### Error Handling
- Use `ILogger<T>` for logging - never use `Console.WriteLine` in production code
- Log levels: `LogDebug`, `LogInformation`, `LogWarning`, `LogError`
- Wrap external service calls in try-catch with proper logging
- Null-check configuration values before use

### Nullable Handling
```csharp
public InputBooleanEntity? OptionalEntity { get; set; }  // Nullable
public required InputBooleanEntity RequiredEntity { get; set; }  // Required

if (entity is null) { /* handle */ }
if (s.New?.State == "on") { /* safe access */ }
```

### Async Patterns
- Prefer `SubscribeAsync()` for async operations in subscriptions
- Use `await Task.Delay()` for delays
- Use `ConfigureAwait(false)` in library code

### Configuration
- Configuration values in `appsettings.json`
- Access via `IConfiguration.GetValue<T>("Section:Key", defaultValue)`
- Map sections to POCO classes: `configuration.GetSection("Section").Get<ConfigClass>()`

## Important Notes

- Entity types are auto-generated in `HomeAssistantGenerated.cs` - reference at netdaemon.xyz for documentation
- Code generation metadata stored in `NetDaemonCodegen/` folder
- Do NOT commit secrets - `appsettings.json` currently contains credentials that should be moved to environment variables or user secrets