using BeaconCore.Config;
using BeaconCore.Events;
using BeaconCore.Hooks;
using BeaconCore.Platform;
using BeaconCore.Platform.Windows;
using BeaconCore.Server;
using BeaconCore.Services;

var builder = WebApplication.CreateBuilder(args);

var config = new BeaconConfig();
builder.Configuration.GetSection("Beacon").Bind(config);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<HookNormalizer>();
builder.Services.AddSingleton<CopilotTranscriptWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotTranscriptWatcher>());

if (config.FakeMode)
{
    builder.Services.AddHostedService<FakeEventEmitter>();
}
else
{
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddSingleton<IPlatformMonitor, WindowsPlatformMonitor>();
    }
    else
    {
        builder.Services.AddSingleton<IPlatformMonitor, NullPlatformMonitor>();
    }

    builder.Services.AddHostedService<PresenceClearService>();
}

var app = builder.Build();

var bus = app.Services.GetRequiredService<EventBus>();

app.MapBeaconEndpoints(bus);

app.Urls.Add($"http://127.0.0.1:{config.Port}");

app.Run();
