using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LlmsTxt.Umbraco.Tests.Composers;

/// <summary>
/// Story 6.0a AC2 (Codex finding #2) — release-guard reflection tests
/// asserting the shipped <c>LlmsTxt.Umbraco.dll</c> contains no spike or
/// template controllers and no unauthorised <c>Ping</c> actions. Run as
/// part of the launch-readiness CI gate via the <c>ReleaseGuard</c>
/// category filter.
/// </summary>
[TestFixture]
[Category("ReleaseGuard")]
public class ReleaseGuardTests
{
    private static readonly Assembly PackageAssembly = typeof(Constants).Assembly;

    /// <summary>
    /// Allow-list of <c>Ping</c> actions intentionally shipped by the package.
    /// Currently empty — the spike controller's <c>Ping</c> was the only one,
    /// and Story 6.0a deletes it. Future stories adding a legitimate <c>Ping</c>
    /// MUST update this allow-list AND the matching production allow-list in
    /// <c>LlmsTxtUmbracoApiComposer.CustomOperationHandler.AllowedControllers</c>.
    /// </summary>
    private static readonly (Type ControllerType, string ActionName)[] AllowedPingActions = Array.Empty<(Type, string)>();

    [Test]
    public void ShippedAssembly_ContainsNoSpikeControllers()
    {
        var hits = PackageAssembly.GetTypes()
            .Where(t => t.Name.Contains("Spike", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName)
            .ToArray();

        Assert.That(hits, Is.Empty,
            $"Spike controllers / types must not ship in {PackageAssembly.GetName().Name}. " +
            $"Found: {string.Join(", ", hits)}");
    }

    [Test]
    public void ShippedAssembly_ContainsNoTemplateControllers()
    {
        var hits = PackageAssembly.GetTypes()
            .Where(t => t.Name.Contains("Template", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName)
            .ToArray();

        Assert.That(hits, Is.Empty,
            $"Template controllers / types must not ship in {PackageAssembly.GetName().Name}. " +
            $"Found: {string.Join(", ", hits)}");
    }

    [Test]
    public void ShippedAssembly_ContainsNoUnauthorisedPingActions()
    {
        // Walk every controller-shaped type in the assembly and surface
        // every action whose [HttpGet] template is "ping" (case-insensitive).
        // Anything not on AllowedPingActions is a release-blocker.
        var unauthorised = PackageAssembly.GetTypes()
            .Where(IsControllerType)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => (ControllerType: t, Method: m)))
            .Where(p => p.Method.GetCustomAttributes<HttpMethodAttribute>(true)
                .Any(a => string.Equals(a.Template, "ping", StringComparison.OrdinalIgnoreCase)))
            .Where(p => Array.IndexOf(
                AllowedPingActions,
                (p.ControllerType, p.Method.Name)) < 0)
            .Select(p => $"{p.ControllerType.FullName}.{p.Method.Name}")
            .ToArray();

        Assert.That(unauthorised, Is.Empty,
            $"Unauthorised Ping actions must not ship in {PackageAssembly.GetName().Name}. " +
            $"Found: {string.Join(", ", unauthorised)}. " +
            $"Add to AllowedPingActions only if the action is intentionally exposed.");
    }

    private static bool IsControllerType(Type t)
    {
        if (t.IsAbstract || !t.IsClass)
        {
            return false;
        }
        // Walk inheritance chain for ControllerBase / Controller — the v17
        // Management API base (`ManagementApiControllerBase`) inherits
        // `Microsoft.AspNetCore.Mvc.Controller` (NOT `ControllerBase`),
        // so checking either covers both surface controllers and management
        // controllers without naming the Umbraco bases explicitly here.
        for (var cursor = t; cursor is not null && cursor != typeof(object); cursor = cursor.BaseType)
        {
            if (cursor == typeof(ControllerBase) || cursor == typeof(Controller))
            {
                return true;
            }
        }
        return false;
    }
}
