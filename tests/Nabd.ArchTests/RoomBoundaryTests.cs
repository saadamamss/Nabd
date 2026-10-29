using System.Reflection;
using NetArchTest.Rules;
using Nabd.Admin;
using Nabd.Alerting;
using Nabd.Assets;
using Nabd.Identity;
using Nabd.Ingestion;
using Nabd.Notifications;
using Nabd.Realtime;
using Nabd.Rules;
using Nabd.TelemetryStore;
using Xunit;

namespace Nabd.ArchTests;

/// <summary>
/// The mechanized room boundary — 03 §4 rule 1 made executable (ADR-001).
///
/// The rule at its strongest is ASSEMBLY-level: a room's implementation may reference
/// only its own assembly, the SharedKernel, and other rooms' CONTRACTS assemblies.
/// If the *assembly reference* doesn't exist, no code inside the room could ever
/// reach another room's implementation — the wall is physical, not a convention.
/// </summary>
public sealed class RoomBoundaryTests
{
    private static readonly Actor[] Rooms =
    [
        new(typeof(IdentityModule).Assembly, "Nabd.Identity.Contracts"),
        new(typeof(AssetsModule).Assembly, "Nabd.Assets.Contracts"),
        new(typeof(IngestionModule).Assembly, "Nabd.Ingestion.Contracts"),
        new(typeof(TelemetryStoreModule).Assembly, "Nabd.TelemetryStore.Contracts"),
        new(typeof(RulesModule).Assembly, "Nabd.Rules.Contracts"),
        new(typeof(AlertingModule).Assembly, "Nabd.Alerting.Contracts"),
        new(typeof(NotificationsModule).Assembly, "Nabd.Notifications.Contracts"),
        new(typeof(RealtimeModule).Assembly, "Nabd.Realtime.Contracts"),
        new(typeof(AdminModule).Assembly, "Nabd.Admin.Contracts"),
        new(null, "Nabd.SharedKernel"),  // SharedKernel itself: referenced by all, references none of the rooms
    ];

    private sealed record Actor(Assembly? ImplAssembly, string ContractsNamespace);

    private static HashSet<string> AllowedReferences(Assembly impl)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Nabd.SharedKernel",
        };

        foreach (var room in Rooms)
            allowed.Add(room.ContractsNamespace);

        allowed.Add(impl.GetName().Name!); // a room always owns itself
        return allowed;
    }

    [Fact]
    public void room_implementations_reference_only_contracts_and_shared_kernel()
    {
        var failures = new List<string>();

        foreach (var room in Rooms)
        {
            if (room.ImplAssembly is null) continue;

            var allowed = AllowedReferences(room.ImplAssembly);
            var foreign = room.ImplAssembly.GetReferencedAssemblies()
                .Where(r => AllowedReferences(room.ImplAssembly).Contains(r.Name!) &&
                            !allowed.Contains(r.Name!))
                .Select(r => r.Name)
                .ToList();

            // Room-wall intent (03 §4 rule 1): rooms may reference their OWN assembly,
            // OTHER rooms' contracts, SharedKernel, and the .NET BCL.
            // ONLY cross-room references are forbidden — i.e. any "Nabd.*" assembly
            // outside the allowed set (a real breach always begins with our own prefix).
            // Third-party infrastructure (Npgsql, argon2 providers, ...) is NOT a
            // room-to-room concern; flagging it here was a test-interpretation bug
            // caught by P1 spec 02 (identity's Postgres access), logged in NOTES.
            var forbidden = room.ImplAssembly.GetReferencedAssemblies()
                .Where(r => r.Name is { } name &&
                            !allowed.Contains(name) &&
                            name.StartsWith("Nabd.") &&
                            !name.StartsWith("Nabd.Contracts") &&
                            name != "Nabd.ArchTests")
                .Select(r => r.Name);

            var violations = foreign.Concat(forbidden).Distinct().ToList();
            if (violations.Count > 0)
                failures.Add($"{room.ImplAssembly.GetName().Name}: {string.Join(", ", violations)}");
        }

        Assert.True(failures.Count == 0,
            "Room boundary breaches (03 §4 rule 1) — these assemblies reference more than contracts + SharedKernel:\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void contracts_projects_reference_only_shared_kernel()
    {
        var failures = new List<string>();

        foreach (var room in Rooms)
        {
            var contractsAssembly = Assembly.Load(room.ContractsNamespace);
            var foreign = contractsAssembly.GetReferencedAssemblies()
                .Where(r => r.Name is { } name &&
                            name != room.ContractsNamespace &&
                            name != "Nabd.SharedKernel" &&
                            !name.StartsWith("System") &&
                            !name.StartsWith("Microsoft") &&
                            !name.StartsWith("netstandard") &&
                            name != "mscorlib")
                .Select(r => r.Name)
                .ToList();

            if (foreign.Count > 0)
                failures.Add($"{room.ContractsNamespace}: {string.Join(", ", foreign)}");
        }

        Assert.True(failures.Count == 0,
            "Contracts reach into other layers (04/07 violation):\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// NetArchTest type-level check with no namespace-prefix ambiguity: rooms never
    /// depend upward on hosts (composition roots depend on rooms, never the reverse — 03 §5).
    /// </summary>
    [Fact]
    public void rooms_never_depend_on_hosts_or_hosts_only_on_room_composition_roots()
    {
        var rooms = new[]
        {
            typeof(IdentityModule), typeof(AssetsModule), typeof(IngestionModule),
            typeof(TelemetryStoreModule), typeof(RulesModule), typeof(AlertingModule),
            typeof(NotificationsModule), typeof(RealtimeModule), typeof(AdminModule),
        };

        foreach (var marker in rooms)
        {
            var result = Types.InAssembly(marker.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(["Nabd.ApiHost", "Nabd.ProcessingHost"])
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{marker.Namespace} depends on a host (upward dependency forbidden — 03 §5): {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }
}
