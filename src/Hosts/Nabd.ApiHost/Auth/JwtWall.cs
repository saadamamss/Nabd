namespace Nabd.ApiHost.Auth;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

// P1 spec 04 — THE definition of how an inbound JWT is trusted (plan/07 §5).
// Program consumes it; the isolation suite validates the very same object —
// one truth, so a suite green can never drift from runtime reality.

public static class JwtWall
{
    /// <summary>Claims arrive VERBATIM (sub/tid/roles). The inbound claim-type
    /// map is off at process start, so the middleware, controllers, and the
    /// auth-wall anatomy test all agree on the names the mint gave them.</summary>
    public static void StopInboundClaimRename() =>
        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

    /// <summary>THE middleware configuration (one definition, law #27): the
    /// JwtBearerOptions path — MapInboundClaims false lives HERE because the
    /// JwtBearer pipeline runs on JsonWebTokenHandler, whose claim map is a
    /// SEPARATE flag from JwtSecurityTokenHandler's (the live-verify 403
    /// lesson of 2026-11-09: mint-side "roles" got renamed by the OTHER map,
    /// RequireRole found nothing, and every walled door answered 403-empty).</summary>
    public static void Configure(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions options)
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = Parameters();
    }

    /// <summary>Signature/issuer/audience/expire-time all fail-shut.
    /// Secret from env ONLY (NFR-08 twelve-factor) — never config files.</summary>
    public static TokenValidationParameters Parameters()
    {
        var hex = Environment.GetEnvironmentVariable("NABD_JWT_SECRET")
            ?? throw new InvalidOperationException("NABD_JWT_SECRET must be set (NFR-08: fail-shut).");

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "nabd-identity",
            ValidateAudience = true,
            ValidAudience = "nabd-api",
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromHexString(hex)),
            ValidateLifetime = true,
            RoleClaimType = "roles",
            ClockSkew = TimeSpan.FromSeconds(30),   // the expired-JWT law means what it says
        };
    }
}