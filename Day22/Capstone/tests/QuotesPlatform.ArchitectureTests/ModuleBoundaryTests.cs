using FluentAssertions;

namespace QuotesPlatform.ArchitectureTests;

/// <summary>
/// THE BOUNDARIES ARE ENFORCED BY TESTS, NOT BY DISCIPLINE.
///
/// A modular monolith is only modular while somebody is watching. Every one of
/// these rules is written down in the design document, and a design document
/// has never once failed a build. These tests fail the build.
/// </summary>
public class ModuleBoundaryTests
{
    [Fact]
    public void No_module_references_another_module()
    {
        var violations = new List<string>();

        foreach (var project in SolutionLayout.ProjectFiles)
        {
            var name = SolutionLayout.NameOf(project);
            var module = SolutionLayout.ModuleOf(name);

            if (module is null)
                continue;

            foreach (var reference in SolutionLayout.ProjectReferencesOf(project))
            {
                var referencedModule = SolutionLayout.ModuleOf(reference);

                if (referencedModule is not null && referencedModule != module)
                    violations.Add($"{name} -> {reference}");
            }
        }

        // If this fails, the fix is an integration event in
        // QuotesPlatform.Contracts, not a project reference. Two modules that
        // reference each other are one module with extra folders.
        violations.Should().BeEmpty(
            "modules talk through Contracts and integration events, never by referencing each other");
    }

    [Fact]
    public void Domain_projects_reference_only_the_shared_kernel()
    {
        foreach (var project in DomainProjects())
        {
            var references = SolutionLayout.ProjectReferencesOf(project);

            references.Should().BeEquivalentTo(
                new[] { "QuotesPlatform.SharedKernel" },
                $"{SolutionLayout.NameOf(project)} is a domain project");
        }
    }

    [Fact]
    public void Domain_projects_have_no_package_references()
    {
        foreach (var project in DomainProjects())
        {
            // Specifically this catches EF Core arriving in a domain project,
            // which is how persistence concerns end up shaping an aggregate --
            // a [Key] attribute here, a virtual there, and the model is now
            // partly a database schema.
            SolutionLayout.PackageReferencesOf(project).Should().BeEmpty(
                $"{SolutionLayout.NameOf(project)} must depend on nothing but the language");
        }
    }

    [Fact]
    public void Domain_projects_do_not_reference_the_integration_contracts()
    {
        foreach (var project in DomainProjects())
        {
            // If the domain could see an integration event, the tempting
            // shortcut is to raise one from inside an entity -- publishing a
            // cross-module fact before the transaction that makes it true has
            // committed. Day 20 exists because of that bug.
            SolutionLayout.ProjectReferencesOf(project)
                .Should().NotContain("QuotesPlatform.Contracts");
        }
    }

    [Fact]
    public void Application_projects_reference_only_their_own_domain_and_the_contracts()
    {
        foreach (var project in ProjectsEndingWith(".Application"))
        {
            var module = SolutionLayout.ModuleOf(SolutionLayout.NameOf(project));

            SolutionLayout.ProjectReferencesOf(project).Should().BeEquivalentTo(new[]
            {
                $"QuotesPlatform.Modules.{module}.Domain",
                "QuotesPlatform.Contracts"
            });
        }
    }

    [Fact]
    public void Infrastructure_projects_reference_only_their_own_application()
    {
        foreach (var project in ProjectsEndingWith(".Infrastructure"))
        {
            var module = SolutionLayout.ModuleOf(SolutionLayout.NameOf(project));

            SolutionLayout.ProjectReferencesOf(project).Should().BeEquivalentTo(new[]
            {
                $"QuotesPlatform.Modules.{module}.Application"
            });
        }
    }

    [Fact]
    public void The_host_references_only_module_infrastructure()
    {
        var host = SolutionLayout.ProjectFiles
            .Single(p => SolutionLayout.NameOf(p) == "QuotesPlatform.Host");

        // A Host that can see a domain type is a Host where the first "quick
        // fix" puts business logic in an endpoint.
        SolutionLayout.ProjectReferencesOf(host)
            .Should().OnlyContain(reference => reference.EndsWith(".Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void The_shared_kernel_and_the_contracts_depend_on_nothing()
    {
        foreach (var name in new[] { "QuotesPlatform.SharedKernel", "QuotesPlatform.Contracts" })
        {
            var project = SolutionLayout.ProjectFiles.Single(p => SolutionLayout.NameOf(p) == name);

            SolutionLayout.ProjectReferencesOf(project).Should().BeEmpty();
            SolutionLayout.PackageReferencesOf(project).Should().BeEmpty(
                $"{name} is referenced by everything, so anything it depends on is depended on by everything");
        }
    }

    private static IEnumerable<string> DomainProjects() => ProjectsEndingWith(".Domain");

    private static IEnumerable<string> ProjectsEndingWith(string suffix) =>
        SolutionLayout.ProjectFiles.Where(p =>
            SolutionLayout.NameOf(p).EndsWith(suffix, StringComparison.Ordinal)
            && SolutionLayout.ModuleOf(SolutionLayout.NameOf(p)) is not null);
}
