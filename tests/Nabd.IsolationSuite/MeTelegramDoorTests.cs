namespace Nabd.IsolationSuite;

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Controllers;
using Nabd.Identity.Contracts;
using Nabd.Notifications.Contracts;
using Nabd.SharedKernel;
using Xunit;

// P4 spec 09 — the self-service severity door's ONE law the room cannot hold itself:
// the difference between "the field is absent" (a malformed request) and "the user
// sent []" (an explicit mute). Found in review 2026-11-22: `?? []` turned the first
// into the second, so one bad client could silence every Telegram alert silently.
// The room's own net (TelegramPushTests) calls SetSeveritiesAsync directly and can
// never see this; the door is where the lie lived.

public sealed class MeTelegramDoorTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MeTelegramController Controller(FakeChannels channels)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", UserId.ToString()),
            new Claim("tid", TenantId.ToString()),
            new Claim("roles", "tenant_admin"),
        ], "test");

        return new MeTelegramController(channels)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    [Fact]
    public async Task a_missing_severities_field_is_422_and_never_reaches_the_room()
    {
        var channels = new FakeChannels();

        var result = await Controller(channels).SetSeverities(
            new MeTelegramController.SetSeveritiesBody(null), CancellationToken.None);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(refused.Value);
        Assert.Equal(
            $$"""{"code":"{{NotificationErrors.InvalidSeverity}}"}""", json);
        Assert.Null(channels.LastSeverities);   // the room never saw a call: nothing was muted
    }

    [Fact]
    public async Task an_explicit_empty_list_is_the_users_own_mute_and_reaches_the_room()
    {
        var channels = new FakeChannels();

        var result = await Controller(channels).SetSeverities(
            new MeTelegramController.SetSeveritiesBody([]), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(channels.LastSeverities);
        Assert.Empty(channels.LastSeverities!);
    }

    private sealed class FakeChannels : ITelegramChannels
    {
        public IReadOnlyList<string>? LastSeverities { get; private set; }

        public Task<Result<TelegramLinkTicket>> IssueLinkCodeAsync(
            CallerContext acting, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<TelegramChannelState>> GetAsync(
            CallerContext acting, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<TelegramChannelState>> UnlinkAsync(
            CallerContext acting, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<TelegramChannelState>> SetSeveritiesAsync(
            CallerContext acting, IReadOnlyList<string> severities, CancellationToken ct = default)
        {
            LastSeverities = severities;
            return Task.FromResult(Result<TelegramChannelState>.Success(
                new TelegramChannelState(false, null, severities)));
        }
    }
}
