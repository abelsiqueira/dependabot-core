using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

using NuGet.Frameworks;
using NuGet.Versioning;

using NuGetUpdater.Core.Discover;

namespace NuGetUpdater.Core.Analyze;

public partial class AnalyzeWorker
{
    public const string AnalysisDirectoryName = "./.dependabot/analysis";

    private readonly Logger _logger;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new RequirementConverter() },
    };

    public AnalyzeWorker(Logger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(string repoRoot, string discoveryPath, string dependencyPath, string analysisDirectory)
    {
        var discovery = await DeserializeJsonFileAsync<WorkspaceDiscoveryResult>(discoveryPath, nameof(WorkspaceDiscoveryResult));
        var dependencyInfo = await DeserializeJsonFileAsync<DependencyInfo>(dependencyPath, nameof(DependencyInfo));
        var startingDirectory = PathHelper.JoinPath(repoRoot, discovery.FilePath);

        // We need to find all projects which have the given dependency. Even in cases that they
        // have it transitively may require that peer dependencies be updated in the project.
        var projectsWithDependency = discovery.Projects
            .Where(p => p.Dependencies.Any(d => d.Name.Equals(dependencyInfo.Name, StringComparison.OrdinalIgnoreCase)))
            .ToImmutableArray();
        var projectFrameworks = projectsWithDependency
            .SelectMany(p => p.TargetFrameworks)
            .Distinct()
            .Select(NuGetFramework.Parse)
            .ToImmutableArray();
        // When updating peer dependencies, we only need to consider top-level dependencies.
        var projectDependencyNames = projectsWithDependency
            .SelectMany(p => p.Dependencies)
            .Where(d => !d.IsTransitive)
            .Select(d => d.Name)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        bool versionComesFromMultiDependencyProperty = DoesDependencyUseMultiDependencyProperty(
            discovery,
            dependencyInfo,
            projectsWithDependency);

        var updatedVersion = await FindUpdatedVersionAsync(
            startingDirectory,
            dependencyInfo,
            projectFrameworks,
            _logger,
            CancellationToken.None);

        var updatedDependencies = updatedVersion is not null
            ? await FindUpdatedDependenciesAsync(
                startingDirectory,
                discovery,
                projectsWithDependency,
                projectFrameworks,
                projectDependencyNames,
                dependencyInfo,
                updatedVersion,
                _logger)
            : [];

        var result = new AnalysisResult
        {
            UpdatedVersion = updatedVersion?.ToNormalizedString() ?? dependencyInfo.Version,
            CanUpdate = updatedVersion is not null,
            VersionComesFromMultiDependencyProperty = versionComesFromMultiDependencyProperty,
            UpdatedDependencies = updatedDependencies,
        };

        await WriteResultsAsync(analysisDirectory, dependencyInfo.Name, result);
    }

    internal static async Task<T> DeserializeJsonFileAsync<T>(string path, string fileType)
    {
        var json = File.Exists(path)
            ? await File.ReadAllTextAsync(path)
            : throw new FileNotFoundException($"{fileType} file not found.", path);

        return JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"{fileType} file is empty.");
    }

    internal static async Task<NuGetVersion?> FindUpdatedVersionAsync(
        string startingDirectory,
        DependencyInfo dependencyInfo,
        ImmutableArray<NuGetFramework> projectFrameworks,
        Logger logger,
        CancellationToken cancellationToken)
    {
        var nugetContext = new NuGetContext(startingDirectory);
        if (!Directory.Exists(nugetContext.TempPackageDirectory))
        {
            Directory.CreateDirectory(nugetContext.TempPackageDirectory);
        }

        var currentVersion = NuGetVersion.Parse(dependencyInfo.Version);

        var versionResult = await VersionFinder.GetVersionsAsync(
            dependencyInfo,
            nugetContext,
            cancellationToken);
        var versions = versionResult.GetVersions();
        var orderedVersions = dependencyInfo.IsVulnerable
            ? versions.OrderBy(v => v) // If we are fixing a vulnerability, then we want the lowest version that is safe.
            : versions.OrderByDescending(v => v); // If we are just updating versions, then we want the highest version possible.

        return await FindFirstCompatibleVersion(
            dependencyInfo.Name,
            currentVersion,
            versionResult,
            orderedVersions,
            projectFrameworks,
            nugetContext,
            logger,
            cancellationToken);
    }

    internal static async Task<NuGetVersion?> FindFirstCompatibleVersion(
        string packageId,
        NuGetVersion currentVersion,
        VersionResult versionResult,
        IEnumerable<NuGetVersion> orderedVersions,
        ImmutableArray<NuGetFramework> projectFrameworks,
        NuGetContext nugetContext,
        Logger logger,
        CancellationToken cancellationToken)
    {
        var source = versionResult.GetPackageSources(currentVersion).First();
        var isCompatible = await CompatibilityChecker.CheckAsync(
            source,
            new(packageId, currentVersion),
            projectFrameworks,
            nugetContext,
            logger,
            cancellationToken);
        if (!isCompatible)
        {
            // If the current package is incompatible, then don't check for compatibility.
            return orderedVersions.First();
        }

        foreach (var version in orderedVersions)
        {
            source = versionResult.GetPackageSources(version).First();
            isCompatible = await CompatibilityChecker.CheckAsync(
                source,
                new(packageId, version),
                projectFrameworks,
                nugetContext,
                logger,
                cancellationToken);

            if (isCompatible)
            {
                return version;
            }
        }

        // Could not find a compatible version
        return null;
    }

    internal static async Task<ImmutableDictionary<NuGetFramework, ImmutableArray<Dependency>>> GetDependenciesAsync(
        string workspacePath,
        string projectPath,
        IEnumerable<NuGetFramework> frameworks,
        Dependency package,
        Logger logger)
    {
        var result = ImmutableDictionary.CreateBuilder<NuGetFramework, ImmutableArray<Dependency>>();
        foreach (var framework in frameworks)
        {
            var dependencies = await MSBuildHelper.GetAllPackageDependenciesAsync(
                workspacePath,
                projectPath,
                framework.ToString(),
                [package],
                logger);
            result.Add(framework, [.. dependencies]);
        }
        return result.ToImmutable();
    }

    internal static async Task<ImmutableArray<Dependency>> FindUpdatedDependenciesAsync(
        string repoRoot,
        WorkspaceDiscoveryResult discovery,
        ImmutableArray<ProjectDiscoveryResult> projectsWithDependency,
        ImmutableArray<NuGetFramework> projectFrameworks,
        ImmutableHashSet<string> projectDependencyNames,
        DependencyInfo dependencyInfo,
        NuGetVersion updatedVersion,
        Logger logger)
    {
        // Determine updated peer dependencies
        var workspacePath = PathHelper.JoinPath(repoRoot, discovery.FilePath);
        // We need any project path so the dependency finder can locate the nuget.config
        var projectPath = projectsWithDependency.First().FilePath;

        // Create distinct list of dependencies taking the highest version of each
        var dependencyResult = await DependencyFinder.GetDependenciesAsync(
            workspacePath,
            projectPath,
            projectFrameworks,
            package: new(dependencyInfo.Name, updatedVersion.ToNormalizedString(), DependencyType.Unknown),
            logger);

        // Filter dependencies by whether any project references them
        return dependencyResult.GetDependencies()
            .Where(dep => projectDependencyNames.Contains(dep.Name))
            .ToImmutableArray();
    }

    internal static bool DoesDependencyUseMultiDependencyProperty(
        WorkspaceDiscoveryResult discovery,
        DependencyInfo dependencyInfo,
        ImmutableArray<ProjectDiscoveryResult> projectsWithDependency)
    {
        var declarationsUsingProperty = projectsWithDependency.SelectMany(p
            => p.Dependencies.Where(d => !d.IsTransitive &&
                d.Name.Equals(dependencyInfo.Name, StringComparison.OrdinalIgnoreCase) &&
                d.EvaluationResult?.RootPropertyName is not null)
            ).ToImmutableArray();
        var allPropertyBasedDependencies = discovery.Projects.SelectMany(p
            => p.Dependencies.Where(d => !d.IsTransitive &&
                !d.Name.Equals(dependencyInfo.Name, StringComparison.OrdinalIgnoreCase) &&
                d.EvaluationResult is not null)
            ).ToImmutableArray();

        return declarationsUsingProperty.Any(d =>
        {
            var property = d.EvaluationResult!.RootPropertyName!;

            return allPropertyBasedDependencies
                .Where(pd => !pd.Name.Equals(dependencyInfo.Name, StringComparison.OrdinalIgnoreCase))
                .Any(pd => pd.EvaluationResult?.RootPropertyName == property);
        });
    }

    internal static async Task WriteResultsAsync(string analysisDirectory, string dependencyName, AnalysisResult result)
    {
        if (!Directory.Exists(analysisDirectory))
        {
            Directory.CreateDirectory(analysisDirectory);
        }

        var resultPath = Path.Combine(analysisDirectory, $"{dependencyName}.json");
        var resultJson = JsonSerializer.Serialize(result, SerializerOptions);
        await File.WriteAllTextAsync(path: resultPath, resultJson);
    }
}
