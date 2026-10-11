using System.Reflection;
using FluentAssertions;
using Xunit;

namespace RetailFlow.Application.UnitTests;

/// <summary>
/// Guards the dependency rule from ADR-0002: Domain depends on nothing, and Application must stay
/// free of EF Core and ASP.NET. These are the cheapest possible tests for the most expensive kind
/// of mistake — a persistence or HTTP concern leaking into business code.
/// </summary>
public class LayerBoundaryTests
{
    private static readonly string[] ForbiddenInApplication =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Data.SqlClient",
        "RetailFlow.Infrastructure",
        "RetailFlow.Api",
    ];

    private static readonly string[] ForbiddenInDomain =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Data.SqlClient",
        "Microsoft.Extensions",
        "FluentValidation",
        "RetailFlow.Application",
        "RetailFlow.Infrastructure",
        "RetailFlow.Api",
    ];

    [Fact]
    public void Application_does_not_reference_persistence_or_web_assemblies()
    {
        var referenced = ReferencedAssemblyNames(typeof(ApplicationAssemblyMarker).Assembly);

        foreach (var forbidden in ForbiddenInApplication)
        {
            referenced.Should().NotContain(
                name => name.StartsWith(forbidden, StringComparison.Ordinal),
                $"the Application layer must not depend on {forbidden} (ADR-0002)");
        }
    }

    [Fact]
    public void Domain_depends_on_nothing_but_the_base_class_library()
    {
        var domain = typeof(Domain.Platform.Money).Assembly;
        var referenced = ReferencedAssemblyNames(domain);

        foreach (var forbidden in ForbiddenInDomain)
        {
            referenced.Should().NotContain(
                name => name.StartsWith(forbidden, StringComparison.Ordinal),
                $"the Domain layer must not depend on {forbidden} (ADR-0002)");
        }
    }

    // A positive check ("Application references Domain") is deliberately absent: the compiler
    // trims assembly references that no code uses, and Application has no use cases yet. It is
    // added in Phase 2 together with the first handlers.

    private static List<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();
}
