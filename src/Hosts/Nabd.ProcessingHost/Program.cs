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

// P4 spec 05: the lifecycle consumer. AlertService is the room's only DB owner.
builder.Services.AddSingleton<AlertingDb>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<IAlerting>(sp => sp.GetRequiredService<AlertService>());
builder.Services.AddSingleton<AlertLifecycle>();

// P4 spec 07: the watchdog reads the SAME tracker/buffer the sink feeds.
builder.Services.AddSingleton<IDeviceStatusTracker>(sp => sp.GetRequiredService<DeviceStatusTracker>());
builder.Services.AddSingleton<OfflineWatchdog>();
builder.Services.AddHostedService<OfflineWatchdogWorker>();

// P4 spec 08: the people the notifications address (Identity owns the read)
builder.Services.AddSingleton<IdentityDb>();
builder.Services.AddSingleton<IUserDirectory, UserDirectory>();

// P4 spec 08: the email channel + its one subscriber
builder.Services.AddSingleton<NotificationsDb>();
builder.Services.AddSingleton<NotificationLog>();            // spec 09: shared evidence writer
builder.Services.AddSingleton<EmailChannelConfig>();
builder.Services.AddSingleton<AlertMessageRenderer>();       // spec 09: channel-aware (was AlertEmailRenderer)
builder.Services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
builder.Services.AddSingleton<EmailQueue>();
builder.Services.AddSingleton(EmailRetryPolicy.Default);     // 2 s / 10 s; a test injects its own
builder.Services.AddSingleton<EmailNotificationSender>();
builder.Services.AddSingleton<INotificationSender>(sp => sp.GetRequiredService<EmailNotificationSender>());
builder.Services.AddSingleton<EmailNotifier>();
builder.Services.AddHostedService<EmailDispatchWorker>();

// P4 spec 09: the Telegram channel — config/token, link service, bot ear, delivery lane
builder.Services.AddSingleton<TelegramChannelConfig>();
builder.Services.AddSingleton<ITelegramTransport, HttpTelegramTransport>();
builder.Services.AddSingleton<TelegramChannels>();
builder.Services.AddSingleton<TelegramQueue>();
builder.Services.AddSingleton(TelegramRetryPolicy.Default);
builder.Services.AddSingleton<TelegramNotificationSender>();
builder.Services.AddSingleton<TelegramNotifier>();
builder.Services.AddHostedService<TelegramDispatchWorker>();
builder.Services.AddHostedService<TelegramBotWorker>();

var host = builder.Build();

// P4 spec 04: build the engine BEFORE any worker starts.
host.Services.GetRequiredService<RulesEngine>();
// spec 05: subscribe BEFORE the pump's first publish.
host.Services.GetRequiredService<AlertLifecycle>();
// spec 08: the email subscriber must exist before the first alert is published.
host.Services.GetRequiredService<EmailNotifier>();
// spec 09: the Telegram subscriber — the same boot-order law.
host.Services.GetRequiredService<TelegramNotifier>();
host.Run();
