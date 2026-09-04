using System.Xml.Linq;

namespace QuotesPlatform.ArchitectureTests;

/// <summary>
/// Finds the Capstone folder from the test binary's location and reads the
/// project graph out of the .csproj files.
/// </summary>
internal static class SolutionLayout
{
    public static string Root { get; } = FindRoot();

    public static IReadOnlyList<string> ProjectFiles { get; } =
        Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .OrderBy(p => p)
            .ToList();

    /// <summary>Project names this project references directly, by file name without extension.</summary>
    public static IReadOnlyList<string> ProjectReferencesOf(string projectFile)
    {
        var document = XDocument.Load(projectFile);

        return document.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .OrderBy(name => name)
            .ToList();
    }

    public static IReadOnlyList<string> PackageReferencesOf(string projectFile)
    {
        var document = XDocument.Load(projectFile);

        return document.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Where(name => name.Length > 0)
            .OrderBy(name => name)
            .ToList();
    }

    public static string NameOf(string projectFile) => Path.GetFileNameWithoutExtension(projectFile);

    /// <summary>"Catalog" for QuotesPlatform.Modules.Catalog.Domain; null for anything else.</summary>
    public static string? ModuleOf(string projectName)
    {
        const string prefix = "QuotesPlatform.Modules.";

        if (!projectName.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var rest = projectName[prefix.Length..];
        var dot = rest.IndexOf('.');

        return dot < 0 ? rest : rest[..dot];
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuotesPlatform.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate QuotesPlatform.slnx above " + AppContext.BaseDirectory);
    }
}
