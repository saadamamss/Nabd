using Nabd.Alerting;
using Nabd.Notifications;
using Nabd.ProcessingHost;
using Nabd.Rules;

var builder = Host.CreateApplicationBuilder(args);

// P4 spec 10 (D10g): the ONE measured vertical — ingestion → rules → alerting
// → notifications. It lives in P4Vertical.cs so the acceptance E2E boots the
// same registration list in-process instead of copying it.
builder.Services.AddP4Vertical();

// The broker ear (MQTT) — deliberately outside the vertical: the E2E never dials EMQX.
builder.Services.AddHostedService<ProcessingWorker>();

// P4 spec 09: the Telegram channel — config/token, link service, bot ear, delivery lane.
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

// P4 spec 04/05/08/09: subscribers and state exist BEFORE the first publish.
host.Services.GetRequiredService<RulesEngine>();        // spec 04: the engine is built before any worker starts
host.Services.GetRequiredService<AlertLifecycle>();     // spec 05: subscribed before the pump's first publish
host.Services.GetRequiredService<EmailNotifier>();      // spec 08: the email subscriber must exist first
host.Services.GetRequiredService<TelegramNotifier>();   // spec 09: the same boot-order law
host.Run();
