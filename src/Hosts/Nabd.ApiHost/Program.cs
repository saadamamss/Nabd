using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o =>
{
    // 03 §8: minimal API surface — no camelCase surprises, enums as strings for log readability
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi();

// ===== Room wiring (composition root — the ONLY place impl assemblies are touched) =====
// P0: Assets' in-memory stub. P1/P2 swap implementations behind the same contracts.
builder.Services.AddSingleton<IDeviceRegistry, InMemoryDeviceRegistry>();
builder.Services.AddSingleton<IdentityDb>();
builder.Services.AddSingleton<ITenantRegistration, TenantRegistration>();
builder.Services.AddSingleton<IUserManagement, UserManagement>();

// Health endpoints (ADR-010; 08 §6): liveness = process up.
// Readiness expands in P1+ to probe DB, queue and broker session (08 §6 readiness list).
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.MapHealthChecks("/health/live");

// TODO(P1+): readiness must check DB reachable, queue reachable, broker session ok (08 §6).
// For P0 it reports Degraded with the checklist so nobody mistakes it for production-ready.
app.MapGet("/health/ready", () => Results.Json(new
{
    status = "degraded",
    note = "P0 skeleton — readiness checks (db/queue/broker) arrive with the compose services (08 §6)",
    version = "0.1.0-p0",
}));

app.Run();
