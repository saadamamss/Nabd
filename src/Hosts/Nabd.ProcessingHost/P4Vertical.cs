namespace Nabd.ProcessingHost;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nabd.Alerting;
using Nabd.Alerting.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Notifications;
using Nabd.Notifications.Contracts;
using Nabd.Rules;
using Nabd.SharedKernel;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// P4 spec 10 (D10g): the ONE P4 composition — ingestion → rules → alerting →
/// notifications. It deliberately excludes the broker ear (ProcessingWorker)
/// and the Telegram lane, so the acceptance E2E can boot the measured vertical
/// in-process without dialing EMQX or a bot. ProcessingHost calls this and
/// adds those two transports; the E2E calls it and adds nothing — the measured
/// path is the production path by construction, not a copied list that drifts.
/// </summary>
public static class P4Vertical
{
    public static IServiceCollection AddP4Vertical(this IServiceCollection services)
    {
        // ONE shared TelemetryStoreDb + ONE shared tracker (spec 01/07 law: two
        // trackers = the watchdog marks a window the sink never fills). TryAdd
        // lets a rig that already owns them — the E2E — keep its instances.
        services.TryAddSingleton<TelemetryStoreDb>();
        services.TryAddSingleton<DeviceStatusTracker>();
        services.TryAddSingleton<IDeviceStatusTracker>(sp => sp.GetRequiredService<DeviceStatusTracker>());
        services.TryAddSingleton<IEventBus, InProcessEventBus>();

        services.AddHostedService<DeviceStatusFlushWorker>();

        services.AddSingleton<RulesDb>();
        services.AddSingleton<RulesEngine>();
        services.AddHostedService<RulesCheckpointWorker>();

        services.AddSingleton<AlertingDb>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<IAlerting>(sp => sp.GetRequiredService<AlertService>());
        services.AddSingleton<AlertLifecycle>();

        services.AddSingleton<OfflineWatchdog>();
        services.AddHostedService<OfflineWatchdogWorker>();

        services.AddSingleton<IdentityDb>();
        services.AddSingleton<IUserDirectory, UserDirectory>();

        services.AddSingleton<NotificationsDb>();
        services.AddSingleton<NotificationLog>();
        services.AddSingleton<EmailChannelConfig>();
        services.AddSingleton<AlertMessageRenderer>();
        services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
        services.AddSingleton<EmailQueue>();
        services.AddSingleton(EmailRetryPolicy.Default);
        services.AddSingleton<EmailNotificationSender>();
        services.AddSingleton<INotificationSender>(sp => sp.GetRequiredService<EmailNotificationSender>());
        services.AddSingleton<EmailNotifier>();
        services.AddHostedService<EmailDispatchWorker>();

        return services;
    }
}