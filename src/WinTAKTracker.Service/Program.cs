using WinTAKTracker.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "WinTAKTracker";
});
builder.Services.AddHostedService<TrackingWorker>();

var host = builder.Build();
await host.RunAsync();
