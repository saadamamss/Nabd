namespace Nabd.IsolationSuite;

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Identity.Contracts;
using Xunit;

// Isolation-suite piece 6 (P1 spec 04): the route-coverage wall — pure
// reflection over the host's controllers, NO HTTP server needed. This is the
// automation of plan/07 §5's deny-by-default: a door without an authority
// name fails here, and the FallbackPolicy fails it at runtime. Two nets.
//
// The matrix-linkage fact runs in the DIRECTION the spec demands: a policy
// must never admit a role the matrix would refuse (wide door ≥ matrix? no —
// the other way: the matrix is always ≥ the door, houses are safer inside).

public sealed class RouteWallTests
{
    // The admitted-role table IS the host's own PolicyBook — no test-side copy
    // (law #27). This test's remaining job: no policy admits a role the matrix
    // would refuse, i.e. the matrix is always ≥ the doors.
    private static IReadOnlyDictionary<string, string[]> PolicyAdmits => PolicyBook.Admits;

    private static IEnumerable<(string Where, MethodInfo Action)> Actions() =>
        // seed via a HOST type — the assembler of the seed decides the assembly;
        // a test-namespace private class reflection-inspects OURSELVES (law 14)
        typeof(Caller).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ControllerBase)) && !t.IsAbstract)
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<ApiControllerAttribute>() is null
                     && m.GetCustomAttributes().Any(a => a.GetType().Name.StartsWith("Http")))
            .Select(m => ($"{m.DeclaringType!.Name}.{m.Name}", m));

    // ---- the anonymous club is defined by the host, not by us -----------------

    private static bool IsAllowAnonymous(MethodInfo m) =>
        m.GetCustomAttribute<AllowAnonymousAttribute>() is not null;

    private static AuthorizeAttribute? Wall(MethodInfo m) =>
        m.GetCustomAttributes<AuthorizeAttribute>().Cast<AuthorizeAttribute>().FirstOrDefault();

    // ---- fact 1: no unguarded door, ever --------------------------------------
    // (the DoD's "lacking-attribute endpoint test" — the route granularity version)

    [Fact]
    public void every_action_is_named_anonymous_or_hits_one_wall_exactly()
    {
        var failures = Actions().Select(a => (a.Where, Why: IsAllowAnonymous(a.Action)
                ? a.Action is { } && Wall(a.Action) is not null
                    ? "cannot be BOTH anonymous and walled"
                    : null
                : Wall(a.Action) switch
                {
                    null => "no wall attribute (deny-by-default violation)",
                    var w when w.Policy is not (Policies.Viewer
                        or Policies.TenantAdmin or Policies.PlatformAdmin)
                        => $"unknown policy '{w.Policy}'",
                    _ => null,
                }))
            .Where(f => f.Why is not null)
            .Select(f => $"{f.Where}: {f.Why}")
            .ToList();

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    // ---- fact 2: the anonymous club is EXACTLY the pre-auth wall ---------------

    [Fact]
    public void anonymous_doors_are_exactly_the_credentials_wall()
    {
        var named = Actions()
            .Where(a => IsAllowAnonymous(a.Action) && Wall(a.Action) is null)
            .Select(a => a.Action.Name)
            .OrderBy(x => x)
            .ToList();

        // spec 05: the reset doors join the pre-auth club (reset_request/reset_confirm)
        Assert.Equal(["Login", "Logout", "Refresh", "Register", "ResetConfirm", "ResetRequest"], named);
    }

    // ---- fact 3: policy never admits what the matrix denies (drift detector) ---

    [Fact]
    public void every_walled_action_is_matrix_legal_for_every_role_it_admits()
    {
        var failures = new List<string>();

        foreach (var (Where, Action) in Actions().Where(a =>
                     Wall(a.Action) is not null && !IsAllowAnonymous(a.Action)))
        {
            var wall = Wall(Action)!;
            var op = Action.GetCustomAttributes()
                .Select(at => at.GetType().GetProperty("Operation") is { } p
                              ? p.GetValue(at) as string
                              : null)
                .FirstOrDefault(op => op is not null);

            if (op is null || !PolicyAdmits.TryGetValue(wall.Policy!, out var roles))
                continue;   // fact 1 already owns malformed doors; this is the matrix check

            foreach (var role in roles)
                if (!AuthorizationMatrix.Allows(role, op))
                    failures.Add($"{Where}: policy admits '{role}' for '{op}' but the matrix refuses");
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    // ---- fact 4: the deny-by-default net is actually armed ---------------------
    // (the attributes-without-registry mirror: walls named + no registry = 500
    // at runtime; the fallback must exist and refuse anonymous callers)

    [Fact]
    public void the_policy_book_arms_every_wall_and_a_fallback()
    {
        var options = new AuthorizationOptions();
        PolicyBook.Register(options);

        Assert.Equal(
            ["platform_admin", Policies.TenantAdmin, Policies.Viewer],
            PolicyBook.Admits.Keys.OrderBy(k => k).ToArray());

        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(options.FallbackPolicy!.Requirements,
            r => r is DenyAnonymousAuthorizationRequirement);
    }
}
