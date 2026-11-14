using System.Text.Json.Serialization;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Nabd.ApiHost.Auth;
using Nabd.Admin.Contracts;
using Nabd.Admin;
using Microsoft.AspNetCore.Authorization;
using Nabd.Ingestion.Contracts;
using Nabd.Ingestion;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;

_ = Nabd.Identity.EnumerationArmor.BurnHash;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o =>
{
    // 03 §8: minimal API surface — no camelCase surprises, enums as strings for log readability
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi();
// Postman's importer speaks OpenAPI 3.0 — a SECOND document pinned at 3.0 so
// the import path works (v1 stays 3.1 for tooling that wants the newest).
builder.Services.AddOpenApi("postman", o =>
    o.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0);

// JWT wall (spec 04): parameters live in JwtWall — one definition, shared
// with the suite; the suite green and the host's runtime view cannot drift.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtWall.Configure);

// The walls' policy registry — from PolicyBook, one definition (spec 04):
// viewer/tenant_admin/platform_admin + FallbackPolicy deny-by-default.
builder.Services.AddAuthorization(PolicyBook.Register);

// ===== Room wiring (composition root — the ONLY place impl assemblies are touched) =====
// P0: Assets' in-memory stub. P1/P2 swap implementations behind the same contracts.
builder.Services.AddSingleton<IdentityDb>();
builder.Services.AddSingleton<ITenantRegistration, TenantRegistration>();
builder.Services.AddSingleton<IUserManagement, UserManagement>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<IUserAuthentication, UserAuthentication>();
builder.Services.AddSingleton<IEmailSender, DevLogEmailSender>();
builder.Services.AddSingleton<AdminDb>();
builder.Services.AddSingleton<IPlatformTenantAdmin, PlatformTenantAdmin>();
builder.Services.AddSingleton<AssetsDb>();
builder.Services.AddSingleton<IAssetCatalog, AssetCatalog>();
builder.Services.AddSingleton<DeviceTokenHasher>();
builder.Services.AddSingleton<IDeviceLifecycle, DeviceLifecycle>();
builder.Services.AddSingleton<ITemplateCatalog,TemplateCatalog>();
builder.Services.AddSingleton<InternalSecrets>();
builder.Services.AddSingleton<IAuthorizationHandler, InternalSecretHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, InternalDenyResultHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IConsumerAccess, ConsumerAccess>();

// ===== P3 spec 05 — the HTTP ingest door's wiring (plan/05 §4, VETO ruling) =====
// The door rides the SAME pipeline + store as the MQTT path; only the transport
// differs. The pipeline instance here carries the HTTP batch cap (500 — §4);
// the MQTT consumer's instance keeps its 32-message law in ProcessingHost.
builder.Services.AddSingleton(sp =>
{
    var edges = new IngestionEdges(sp.GetRequiredService<AssetsDb>());
    return new ValidationPipeline(edges, edges, maxReadingsPerBatch: 500);
});
builder.Services.AddSingleton<TelemetryStoreDb>();
builder.Services.AddSingleton<DeviceStatusTracker>();
// the tracker's CONTRACT rides beside its concrete type: the MQTT sink marks
// via the concrete, the HTTP door via the port — ONE shared buffer either way.
builder.Services.AddSingleton<IDeviceStatusTracker>(sp => sp.GetRequiredService<DeviceStatusTracker>());
builder.Services.AddSingleton<IReadingsStore>(sp => new PostgresReadingsStore(
    sp.GetRequiredService<TelemetryStoreDb>(),
    sp.GetRequiredService<IConfiguration>().GetValue("Ingestion:BatchSize", 500)));
builder.Services.AddHostedService<Nabd.TelemetryStore.DeviceStatusFlushWorker>();
builder.Services.AddSingleton<DeviceRateLimiter>();
builder.Services.AddSingleton<HttpIngestDoor>();
// Health endpoints (ADR-010; 08 §6): liveness = process up.
// Readiness expands in P1+ to probe DB, queue and broker session (08 §6 readiness list).
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Dev metadata (OpenAPI doc for Postman import) — anonymous like the health
    // doors: it describes the API, it never calls the rooms. Also a FallbackPolicy
    // 401 victim until pinned (2026-11-09: Postman import could not read it).
    app.MapOpenApi().AllowAnonymous();
}

app.MapControllers();
app.UseAuthentication();
app.UseAuthorization();

// Health doors are PRE-auth by design (liveness/readiness probes are unauthenticated
// infrastructure calls). FallbackPolicy deny-by-default would 401 them — exempt here.
// (RouteWallTests reflect controllers only; these two stay outside that net by shape.)
app.MapHealthChecks("/health/live").AllowAnonymous();

app.MapGet("/health/ready", () => Results.Json(new
{
    status = "degraded",
    note = "P0 skeleton — readiness checks (db/queue/broker) arrive with the compose services (08 §6)",
    version = "0.1.0-p0",
})).AllowAnonymous();

app.Run();

// Test-visible Program (spec 07): the HTTP deny-matrix suite bootstraps the
// REAL host in-process (WebApplicationFactory). A top-level Program compiles
// to an internal class the test assembly cannot name — the partial makes it
// public for tests only, and adds zero runtime surface (NFR-06).
public partial class Program { }
