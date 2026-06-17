using System.Linq;
using System.Reflection;

namespace Umbraco.Community.AiVisibility.Core.Tests;

/// <summary>
/// Story 9.1 AC3 — Core's dependency boundary, enforced as a test in addition to
/// the compile-time MSBuild guard (<c>GuardCoreDependencyBoundary</c> in
/// Umbraco.Community.AiVisibility.Core.csproj). Belt-and-suspenders: the build
/// target fails the build on a forbidden reference; this test fails CI/test runs
/// even if a future edit disables the target. Both protect against the Epic 9
/// boundary contract (epics.md:2677) being violated by drift.
/// </summary>
[TestFixture]
public sealed class CoreDependencyBoundaryTests
{
    // The web/API pipeline assemblies Core MUST NOT reference. Matched by prefix
    // so e.g. Umbraco.Cms.Web.Common / Umbraco.Cms.Api.Management are both caught.
    private static readonly string[] ForbiddenReferencePrefixes =
    {
        "Umbraco.Cms.Web",
        "Umbraco.Cms.Api",
        "Microsoft.AspNetCore",
    };

    // Microsoft.AspNetCore.Cryptography.* is a password-hashing utility pulled
    // transitively by Umbraco.Cms.Infrastructure — it is NOT a web-pipeline
    // assembly, so it is exempt from the AspNetCore ban above.
    private static readonly string[] AllowedReferencePrefixes =
    {
        "Microsoft.AspNetCore.Cryptography",
    };

    [Test]
    public void CoreAssembly_DoesNotReference_AnyWebOrApiPipelineAssembly()
    {
        // typeof(Constants) is a public Core type (moved in Story 9.1) — anchors
        // the reflection to the Core assembly.
        var coreAssembly = typeof(global::Umbraco.Community.AiVisibility.Constants).Assembly;

        var forbidden = coreAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenReferencePrefixes.Any(
                       prefix => name.StartsWith(prefix, System.StringComparison.Ordinal))
                   && !AllowedReferencePrefixes.Any(
                       prefix => name.StartsWith(prefix, System.StringComparison.Ordinal)))
            .OrderBy(name => name)
            .ToList();

        Assert.That(
            forbidden,
            Is.Empty,
            $"Core ({coreAssembly.GetName().Name}) references forbidden web/API pipeline assemblies: "
            + string.Join(", ", forbidden)
            + ". Core must depend only on Umbraco.Cms.Core + Umbraco.Cms.Infrastructure + the extraction NuGets "
            + "(Epic 9 boundary contract, epics.md:2677).");
    }
}
