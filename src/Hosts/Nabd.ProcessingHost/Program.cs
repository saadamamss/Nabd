using Nabd.ProcessingHost;
using Nabd.Rules;
using Nabd.SharedKernel;
using Nabd.TelemetryStore;

var builder = Host.CreateApplicationBuilder(args);

// ONE shared TelemetryStoreDb + ONE shared tracker: the sink's marks and the
// flush worker's window must be the SAME buffer — two instances would flush
// an empty dictionary forever (silent data hole).
builder.Services.AddSingleton<TelemetryStoreDb>();
builder.Services.AddSingleton<DeviceStatusTracker>();

builder.Services.AddHostedService<ProcessingWorker>();
builder.Services.AddHostedService<DeviceStatusFlushWorker>();

builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
builder.Services.AddSingleton<RulesDb>();
builder.Services.AddSingleton<RulesEngine>();
builder.Services.AddHostedService<RulesCheckpointWorker>();

var host = builder.Build();

// P4 spec 04: build the engine BEFORE any worker starts — its CommittedBatch
// subscription must be live before the pump's first publish, and the checkpoint
// worker is not a guarantee of ordering.
host.Services.GetRequiredService<RulesEngine>();

host.Run();