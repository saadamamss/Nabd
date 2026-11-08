namespace Nabd.IsolationSuite;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Nabd.ApiHost.Auth;
using Nabd.Identity.Contracts;
using Xunit;

// Isolation-suite piece 6 (P1 spec 04): the JWT validation lane tests the
// VERY TokenValidationParameters the host uses — because Program now consumes
// JwtWall, single definition, no drift (spec mechanics "one source of truth").
// No DB, no HTTP: mint → validate → map, exactly the middleware's three steps.
//
// The env contract is process-wide (fixture uses the same law), so this class
// self-provisions the suite-local secret it needs; it never reads repo files.

public sealed class JwtWallTests
{
    private const string SuiteSecret =
        "000102030405060708090a0b0c0d0e0f" + "101112131415161718191a1b1c1d1e1f";

    static JwtWallTests()
    {
        Environment.SetEnvironmentVariable("NABD_JWT_SECRET", SuiteSecret);
        JwtWall.StopInboundClaimRename();   // the host's second config step — tests walk the same path, no drift
    }

    private static Guid TenantClaimed() => Guid.NewGuid();

    private static string Mint(
        Guid? userId = null, Guid? tenantId = null, string role = "tenant_admin",
        string? audience = "nabd-api", string? issuer = "nabd-identity",
        DateTime? expires = null, DateTime? notBefore = null, SecurityKey? signWith = null)
    {
        var claims = new List<Claim>();
        if (userId is not null) claims.Add(new Claim("sub", userId.Value.ToString()));
        if (tenantId is not null) claims.Add(new Claim("tid", tenantId.Value.ToString()));
        claims.Add(new Claim("roles", role));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Audience = audience,
            Issuer = issuer,
            // Kind=Utc (DateTime.UtcNow, NOT DateTimeOffset → .DateTime): an
            // Unspecified timestamp gets localized at encode-time — a 15-min
            // future expiry was shipped as 105 min PAST, machine-local +2 trick
            Expires = expires ?? DateTime.UtcNow.AddMinutes(15),
            NotBefore = notBefore,   // legally-expired tokens need a past NotBefore too
            SigningCredentials = new SigningCredentials(
                signWith ?? new SymmetricSecurityKey(Convert.FromHexString(SuiteSecret)),
                SecurityAlgorithms.HmacSha256),   // KEY MATERIAL: hex-decoded, like the host — a UTF8 pass would mint tokens nobody can validate
        };

        return new JwtSecurityTokenHandler().CreateToken(descriptor)
            is JwtSecurityToken token
            ? token.RawData
            : throw new InvalidOperationException("mint failed");
    }

    private static ClaimsPrincipal Validate(string token) =>
        new JwtSecurityTokenHandler().ValidateToken(token, JwtWall.Parameters(), out _);

    private static Guid MintUser() => Guid.NewGuid();

    // ---- the happy lane: verbatim claims, mapped exactly once ----------------

    [Fact]
    public void good_signature_yields_a_caller_with_verbatim_claims()
    {
        var userId = MintUser();
        var tenantId = TenantClaimed();

        var caller = Caller.FromUser(Validate(Mint(userId, tenantId, "tenant_admin")));

        Assert.NotNull(caller);
        Assert.Equal(userId, caller!.UserId);
        Assert.Equal(tenantId, caller.TenantId);
        Assert.Equal(["tenant_admin"], caller.RoleCodes);   // verbatim — the inbound map is OFF
    }

    // ---- fail-shut lanes (each one is a door upstream agents push on) --------

    [Fact]
    public void expired_token_never_reaches_a_door() =>
        // the spec-03 deferred script, now honest: the validation lane itself refuses
        Assert.ThrowsAny<SecurityTokenExpiredException>(
            () => Validate(Mint(
                expires: DateTime.UtcNow.AddHours(-1),
                notBefore: DateTime.UtcNow.AddHours(-2))));

    [Fact]
    public void forged_signature_never_reaches_a_door() =>
        Assert.ThrowsAny<SecurityTokenInvalidSignatureException>(() => Validate(
            Mint(signWith: new SymmetricSecurityKey(
                Convert.FromHexString(new string('a', 64))))));   // a REAL 32-byte rival key

    [Fact]
    public void wrong_audience_never_reaches_a_door() =>
        Assert.ThrowsAny<SecurityTokenInvalidAudienceException>(() => Validate(
            Mint(audience: "some-other-service")));

    [Fact]
    public void tampered_payload_never_reaches_a_door()
    {
        var token = Mint();
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("""{"sub":"1","tid":"1","roles":["platform_admin"]}"""))}.{parts[2]}";
        Assert.ThrowsAny<SecurityTokenInvalidSignatureException>(() => Validate(tampered));
    }

    [Fact]
    public void missing_tid_validates_but_never_becomes_a_caller()
    {
        // the second deferred spec-03 script: the wall's math is fine — the claim
        // MAPPING is the fail-shut gate (Caller.FromUser refuses to invent a tenant)
        var principal = Validate(Mint(tenantId: null));
        Assert.Null(Caller.FromUser(principal));
    }
}
