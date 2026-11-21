using Nabd.Alerting;
using Nabd.Alerting.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Notifications;
using Nabd.Notifications.Contracts;
using Nabd.ProcessingHost;
using Nabd.Rules;
using Nabd.SharedKernel;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;

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
// P4 spec 05: the lifecycle consumer. AlertService is the room's only DB owner;
// the interface registration keeps the doors' contract identical to ApiHost's.
builder.Services.AddSingleton<AlertingDb>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<IAlerting>(sp => sp.GetRequiredService<AlertService>());
builder.Services.AddSingleton<AlertLifecycle>();

// P4 spec 07: the watchdog reads the SAME tracker/buffer the sink feeds (a second
// instance would see no marks and call every device dead). Cadence is the host's.
builder.Services.AddSingleton<IDeviceStatusTracker>(sp => sp.GetRequiredService<DeviceStatusTracker>());
builder.Services.AddSingleton<OfflineWatchdog>();
builder.Services.AddHostedService<OfflineWatchdogWorker>();

// P4 spec 08: the people the notifications address (Identity owns the read)
builder.Services.AddSingleton<IdentityDb>();
builder.Services.AddSingleton<IUserDirectory, UserDirectory>();

// P4 spec 08: the email channel + its one subscriber
builder.Services.AddSingleton<NotificationsDb>();
builder.Services.AddSingleton<EmailChannelConfig>();
builder.Services.AddSingleton<AlertEmailRenderer>();
builder.Services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
builder.Services.AddSingleton<EmailQueue>();
builder.Services.AddSingleton(EmailRetryPolicy.Default);   // 2 s / 10 s; a test injects its own
builder.Services.AddSingleton<EmailNotificationSender>();
builder.Services.AddSingleton<INotificationSender>(sp => sp.GetRequiredService<EmailNotificationSender>());
builder.Services.AddSingleton<EmailNotifier>();
builder.Services.AddHostedService<EmailDispatchWorker>();   // the SMTP side of the queue

var host = builder.Build();

// P4 spec 04: build the engine BEFORE any worker starts — its CommittedBatch
// subscription must be live before the pump's first publish, and the checkpoint
// worker is not a guarantee of ordering.
host.Services.GetRequiredService<RulesEngine>();
// spec 05: subscribe BEFORE the pump's first publish — the same boot-order law as
// the engine (an event published with no subscriber is gone forever).
host.Services.GetRequiredService<AlertLifecycle>();
// spec 08: the email subscriber must exist before the first alert is published.
host.Services.GetRequiredService<EmailNotifier>();
host.Run();