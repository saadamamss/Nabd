using Nabd.ProcessingHost;
using Nabd.TelemetryStore;

var builder = Host.CreateApplicationBuilder(args);

// ONE shared TelemetryStoreDb + ONE shared tracker: the sink's marks and the
// flush worker's window must be the SAME buffer — two instances would flush
// an empty dictionary forever (silent data hole).
builder.Services.AddSingleton<TelemetryStoreDb>();
builder.Services.AddSingleton<DeviceStatusTracker>();

builder.Services.AddHostedService<ProcessingWorker>();
builder.Services.AddHostedService<DeviceStatusFlushWorker>();

var host = builder.Build();
host.Run();