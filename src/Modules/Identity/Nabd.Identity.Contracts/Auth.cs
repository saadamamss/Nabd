using Nabd.SharedKernel;

namespace Nabd.Identity.Contracts;

public sealed record LoginRequest(string Email, string Password, string? ClientIp = null);

public sealed record LoginResult(UserSnapshot User, string AccessToken, string RefreshToken);

public sealed record RefreshRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetPasswordRequest(string Email, string? ClientIp = null);

public sealed record ResetConfirmRequest(string Token, string NewPassword);

public sealed record EmailMessage(string To, string Subject, string BodyText);

public interface IUserAuthentication
{
    Task<Result<LoginResult>> LoginAsync(LoginRequest request, CancellationToken ct = default);

    Task<Result<LoginResult>> RefreshAsync(RefreshRequest request, CancellationToken ct = default);

    Task<Result<Unit>> LogoutAsync(RefreshRequest request, CancellationToken ct = default);

    Task<Result<Unit>> ChangePasswordAsync(CallerContext acting, Guid tenantId, Guid userId, ChangePasswordRequest request, CancellationToken ct = default);

    Task<Result<Unit>> RequestPasswordResetAsync(ResetPasswordRequest request, CancellationToken ct = default);

    Task<Result<Unit>> ConfirmPasswordResetAsync(ResetConfirmRequest request, CancellationToken ct = default);
}


public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}