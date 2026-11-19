using Microsoft.Extensions.Logging;
using Nabd.Alerting.Contracts;
using Nabd.Rules.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Alerting;

/// <summary>
/// The alert lifecycle consumer (P4 spec 05, plan/06 §5.1–5.3). Subscribes ITSELF on
/// the D4 in-process bus — the same pattern as RulesEngine — so the ProcessingHost
/// composition is one eager resolve, not a hand-wired chain of callbacks.
///
/// Notification rule: AlertRaised/AlertResolved are published by THIS class, only
/// after AlertService committed the row (no receipt before the durable outcome).
/// Suppressed/deduplicated crossings publish nothing — silence is the point; the
/// evidence lives in alert_events (plan/06 §7).
/// </summary>
public sealed class AlertLifecycle : IDisposable
{
    private readonly AlertService _alerts;
    private readonly IEventBus _bus;
    private readonly ILogger<AlertLifecycle> _logger;
    private readonly IDisposable _crossings;
    private readonly IDisposable _restores;

    private long _opened;
    private long _deduplicated;
    private long _suppressed;
    private long _resolved;

    public AlertLifecycle(AlertService alerts, IEventBus bus, ILogger<AlertLifecycle> logger)
    {
        _alerts = alerts;
        _bus = bus;
        _logger = logger;
        _crossings = bus.Subscribe<CrossingDetected>(OnCrossingAsync);
        _restores = bus.Subscribe<ConditionRestored>(OnRestoredAsync);
    }

    /// <summary>Observability (§11) — what this process's lifecycle actually did.</summary>
    public long Opened => Interlocked.Read(ref _opened);
    public long Deduplicated => Interlocked.Read(ref _deduplicated);
    public long Suppressed => Interlocked.Read(ref _suppressed);
    public long Resolved => Interlocked.Read(ref _resolved);

    public void Dispose()
    {
        _crossings.Dispose();
        _restores.Dispose();
    }

    private async Task OnCrossingAsync(CrossingDetected evt, CancellationToken ct)
    {
        try
        {
            var result = await _alerts.RaiseAsync(evt.Rule, evt.Reading, ct);
            if (!result.IsSuccess)
            {
                // The bus swallows subscriber failures by law — so this room says it
                // out loud. A crossing that cannot be persisted is never silently lost.
                _logger.LogError(
                    "alert not persisted for rule {RuleId} / device {DeviceId}: {Error}",
                    evt.Rule.RuleId, evt.Reading.DeviceId, result.Error);
                return;
            }

            var (outcome, alert) = (result.Value.Outcome, result.Value.Alert);
            switch (outcome)
            {
                case AlertRaiseOutcome.Opened:
                case AlertRaiseOutcome.Reraised:
                    Interlocked.Increment(ref _opened);
                    // spec 08's Notifications + P5's hub subscribe HERE (plan/06 §5.1).
                    await _bus.PublishAsync(new AlertRaised(alert), ct);
                    break;

                case AlertRaiseOutcome.Deduplicated:
                    Interlocked.Increment(ref _deduplicated);
                    break;

                case AlertRaiseOutcome.Suppressed:
                    Interlocked.Increment(ref _suppressed);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;      // a real shutdown, not a subscriber bug
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "alert lifecycle failed for rule {RuleId} / device {DeviceId}",
                evt.Rule.RuleId, evt.Reading.DeviceId);
        }
    }

    private async Task OnRestoredAsync(ConditionRestored evt, CancellationToken ct)
    {
        try
        {
            var resolved = await _alerts.RestoreAsync(evt.Rule, evt.Reading, ct);
            if (resolved is null) return;      // nothing was open: a silent spike stays silent

            Interlocked.Increment(ref _resolved);
            await _bus.PublishAsync(new AlertResolved(
                resolved.TenantId, resolved.AlertId, evt.Reading.TimeUtc, "condition_restored"), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "alert auto-resolve failed for rule {RuleId} / device {DeviceId}",
                evt.Rule.RuleId, evt.Reading.DeviceId);
        }
    }
}