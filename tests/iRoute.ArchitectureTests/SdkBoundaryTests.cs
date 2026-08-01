using System.Xml.Linq;

namespace iRoute.ArchitectureTests;

public sealed class SdkBoundaryTests
{
    [Fact]
    public void DotNetSdkDependsOnlyOnPublicContracts()
    {
        var project = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "iRoute.Sdk.DotNet",
            "iRoute.Sdk.DotNet.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(item => item.Attribute("Include")!.Value.Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["../iRoute.Contracts/iRoute.Contracts.csproj"], references);
    }

    [Fact]
    public void CliDelegatesOnlyToDotNetSdk()
    {
        var project = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "iRoute.Cli",
            "iRoute.Cli.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(item => item.Attribute("Include")!.Value.Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["../iRoute.Sdk.DotNet/iRoute.Sdk.DotNet.csproj"], references);
    }

    [Fact]
    public void OfficialSdkSourcesDoNotReferenceRuntimeImplementations()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "sdks"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(root, "src", "iRoute.Sdk.DotNet"),
                "*.cs",
                SearchOption.TopDirectoryOnly))
            .Where(path => new[] { ".cs", ".ts", ".py", ".java", ".php", ".rs" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}target{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var source = string.Join('\n', files.Select(File.ReadAllText));

        foreach (var internalNamespace in new[]
                 {
                     "iRoute.Core",
                     "iRoute.Runtime",
                     "iRoute.Infrastructure",
                     "iRoute.Api"
                 })
        {
            Assert.DoesNotContain(internalNamespace, source, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "iRoute.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
