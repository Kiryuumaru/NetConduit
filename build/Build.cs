using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using NukeBuildHelpers;
using NukeBuildHelpers.Common.Attributes;
using NukeBuildHelpers.Common.Enums;
using NukeBuildHelpers.Entry;
using NukeBuildHelpers.Entry.Extensions;
using NukeBuildHelpers.Runner.Abstraction;
using Serilog;
using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;

class Build : BaseNukeBuildHelpers
{
    public static int Main() => Execute<Build>(x => x.Interactive);

    public override string[] EnvironmentBranches { get; } = ["prerelease", "master"];

    public override string MainEnvironmentBranch => "master";

    DeploymentAppSpec[] DeploymentApps =>
    [
        new DeploymentAppSpec
        {
            AppId = "net_conduit",
            ProjectName = "NetConduit",
            ProjectTestName = "NetConduit.UnitTests"
        },
        new DeploymentAppSpec
        {
            AppId = "net_conduit_transport_tcp",
            ProjectName = "NetConduit.Transport.Tcp",
            ProjectTestName = "NetConduit.Transport.Tcp.IntegrationTests"
        },
        new DeploymentAppSpec
        {
            AppId = "net_conduit_transport_websocket",
            ProjectName = "NetConduit.Transport.WebSocket",
            ProjectTestName = "NetConduit.Transport.WebSocket.IntegrationTests"
        },
        new DeploymentAppSpec
        {
            AppId = "net_conduit_transport_udp",
            ProjectName = "NetConduit.Transport.Udp",
            ProjectTestName = "NetConduit.Transport.Udp.IntegrationTests"
        },
        new DeploymentAppSpec
        {
            AppId = "net_conduit_transport_ipc",
            ProjectName = "NetConduit.Transport.Ipc",
            ProjectTestName = "NetConduit.Transport.Ipc.IntegrationTests"
        },
        new DeploymentAppSpec
        {
            AppId = "net_conduit_transport_quic",
            ProjectName = "NetConduit.Transport.Quic",
            ProjectTestName = "NetConduit.Transport.Quic.IntegrationTests"
        }
    ];

    [SecretVariable("NUGET_AUTH_TOKEN")]
    readonly string? NuGetAuthToken;

    [SecretVariable("GITHUB_TOKEN")]
    readonly string? GithubToken;

    TestEntry TestEntry => _ => _
        .RunnerOS(RunnerOS.Ubuntu2204)
        .Matrix(DeploymentApps, (test, spec) => test
            .AppId(spec.AppId)
            .WorkflowId($"{spec.AppId}_test")
            .DisplayName($"Test {spec.ProjectName}")
            .ExecuteBeforeBuild(true)
            .Execute(() =>
            {
                var projectFile = RootDirectory / "tests" / spec.ProjectTestName / $"{spec.ProjectTestName}.csproj";
                DotNetTasks.DotNetBuild(_ => _
                    .SetProjectFile(projectFile)
                    .SetProperty("UseLocalNetConduit", true)
                    .SetConfiguration("Release"));
                var runsettingsPath = RootDirectory / "tests" / spec.ProjectTestName / "ci.runsettings";
                var settingsArg = runsettingsPath.FileExists() ? $"--settings {runsettingsPath} " : "";
                var baseArgs =
                    "--no-build " +
                    "--logger \"GitHubActions;summary.includePassedTests=true;summary.includeSkippedTests=true\" " +
                    settingsArg +
                    "-- " +
                    "RunConfiguration.CollectSourceInformation=true ";
                if (spec.ProjectTestName == "NetConduit.UnitTests")
                {
                    var categories = DiscoverTestCategories(projectFile);
                    if (categories.Length == 0)
                    {
                        RunDotNetTest(projectFile, null, baseArgs);
                        return;
                    }

                    RunDotNetTest(projectFile, BuildUncategorizedFilter(categories), baseArgs);

                    var categoryRuns = categories
                        .Select(category => new TestCategoryRun(category, CountTests(projectFile, $"Category={category}")))
                        .Where(run => run.TestCount > 0)
                        .OrderByDescending(run => run.TestCount)
                        .ThenBy(run => run.Category, StringComparer.Ordinal)
                        .ToArray();

                    Log.Information("Discovered {Count} categorized unit test groups: {Groups}",
                        categoryRuns.Length, string.Join(", ", categoryRuns.Select(run => $"{run.Category}={run.TestCount}")));

                    foreach (var categoryRun in categoryRuns)
                        RunDotNetTest(projectFile, $"Category={categoryRun.Category}", baseArgs);
                }
                else
                {
                    DotNetTasks.DotNetTest(_ => _
                        .SetProcessAdditionalArguments(baseArgs)
                        .SetProjectFile(projectFile)
                        .SetConfiguration("Release"));
                }
            }));

    BuildEntry BuildEntry => _ => _
        .RunnerOS(RunnerOS.Ubuntu2204)
        .Matrix(DeploymentApps, (test, spec) => test
            .AppId(spec.AppId)
            .WorkflowId($"{spec.AppId}_build")
            .DisplayName($"Build {spec.ProjectName}")
            .Execute(context =>
            {
                var app = context.Apps.Values.First();
                string version = app.AppVersion.Version.ToString()!;
                string? releaseNotes = null;
                if (app.BumpVersion != null)
                {
                    version = app.BumpVersion.Version.ToString();
                    releaseNotes = app.BumpVersion.ReleaseNotes;
                }
                else if (app.PullRequestVersion != null)
                {
                    version = app.PullRequestVersion.Version.ToString();
                }
                app.OutputDirectory.DeleteDirectory();
                DotNetTasks.DotNetClean(_ => _
                    .SetProject(RootDirectory / "src" / spec.ProjectName / $"{spec.ProjectName}.csproj"));
                DotNetTasks.DotNetBuild(_ => _
                    .SetProjectFile(RootDirectory / "src" / spec.ProjectName / $"{spec.ProjectName}.csproj")
                    .SetProperty("UseLocalNetConduit", false)
                    .SetConfiguration("Release"));
                DotNetTasks.DotNetPack(_ => _
                    .SetProject(RootDirectory / "src" / spec.ProjectName / $"{spec.ProjectName}.csproj")
                    .SetConfiguration("Release")
                    .SetNoRestore(true)
                    .SetNoBuild(true)
                    .SetIncludeSymbols(true)
                    .SetSymbolPackageFormat("snupkg")
                    .SetVersion(version)
                    .SetPackageReleaseNotes(NormalizeReleaseNotes(releaseNotes))
                    .SetProperty("UseLocalNetConduit", false)
                    .SetOutputDirectory(app.OutputDirectory));
            }));

    PublishEntry PublishEntry => _ => _
        .RunnerOS(RunnerOS.Ubuntu2204)
        .Matrix(DeploymentApps, (test, spec) => test
            .AppId(spec.AppId)
            .WorkflowId($"{spec.AppId}_publish")
            .DisplayName($"Publish {spec.ProjectName}")
            .Execute(async context =>
            {
                var app = context.Apps.Values.First();
                if (app.RunType == RunType.Bump)
                {
                    DotNetTasks.DotNetNuGetPush(_ => _
                        .SetSource("https://nuget.pkg.github.com/kiryuumaru/index.json")
                        .SetApiKey(GithubToken)
                        .SetTargetPath(app.OutputDirectory / "**"));
                    DotNetTasks.DotNetNuGetPush(_ => _
                        .SetSource("https://api.nuget.org/v3/index.json")
                        .SetApiKey(NuGetAuthToken)
                        .SetTargetPath(app.OutputDirectory / "**"));
                    await AddReleaseAsset(app.OutputDirectory, app.AppId);
                }
            }));

    Target Clean => _ => _
        .Executes(() =>
        {
            foreach (var path in RootDirectory.GetFiles("**", 99).Where(i => i.Name.EndsWith(".csproj")))
            {
                if (path.Name == "_build.csproj")
                {
                    continue;
                }
                Log.Information("Cleaning {path}", path);
                (path.Parent / "bin").DeleteDirectory();
                (path.Parent / "obj").DeleteDirectory();
            }
            (RootDirectory / ".vs").DeleteDirectory();
        });

    private static string BuildUncategorizedFilter(string[] categories) =>
        string.Join("&", categories.Select(category => $"Category!={category}"));

    private static string[] DiscoverTestCategories(AbsolutePath projectFile)
    {
        var assemblyPath = GetTestAssemblyPath(projectFile);
        var assemblyDirectory = Path.GetDirectoryName(assemblyPath.ToString())
            ?? throw new InvalidOperationException($"Could not determine assembly directory for {assemblyPath}");
        var loadContext = new AssemblyLoadContext($"TestCategoryDiscovery-{Guid.NewGuid()}", isCollectible: true);
        loadContext.Resolving += (_, assemblyName) =>
        {
            var candidate = Path.Combine(assemblyDirectory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
        };

        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath.ToString());
            var categories = GetLoadableTypes(assembly)
                .SelectMany(type => GetTraitCategories(type)
                    .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .SelectMany(GetTraitCategories)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(category => category, StringComparer.Ordinal)
                .ToArray();

            Log.Information("Discovered {Count} unit test categories: {Categories}",
                categories.Length, string.Join(", ", categories));
            return categories;
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static AbsolutePath GetTestAssemblyPath(AbsolutePath projectFile)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectFile.ToString());
        var targetFramework = GetPrimaryTargetFramework(projectFile);
        var assemblyPath = projectFile.Parent / "bin" / "Release" / targetFramework / (projectName + ".dll");
        if (!assemblyPath.FileExists())
            throw new InvalidOperationException($"Expected built test assembly at {assemblyPath}");

        return assemblyPath;
    }

    private static string GetPrimaryTargetFramework(AbsolutePath projectFile)
    {
        var document = XDocument.Load(projectFile.ToString());
        var targetFramework = document.Descendants("TargetFramework")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(targetFramework))
            return targetFramework;

        var targetFrameworks = document.Descendants("TargetFrameworks")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(targetFrameworks))
            throw new InvalidOperationException($"No TargetFramework or TargetFrameworks found in {projectFile}");

        return targetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First();
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types
                .Where(type => type is not null)
                .Cast<Type>()
                .ToArray();
        }
    }

    private static string[] GetTraitCategories(MemberInfo member)
    {
        return member.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName == "Xunit.TraitAttribute")
            .Select(GetCategoryValue)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .ToArray();
    }

    private static string GetCategoryValue(CustomAttributeData attribute)
    {
        if (attribute.ConstructorArguments.Count != 2)
            return string.Empty;

        var name = attribute.ConstructorArguments[0].Value as string;
        if (!string.Equals(name, "Category", StringComparison.Ordinal))
            return string.Empty;

        return attribute.ConstructorArguments[1].Value as string ?? string.Empty;
    }

    private static int CountTests(AbsolutePath projectFile, string filter)
    {
        var proc = Process.Start(new ProcessStartInfo("dotnet",
            $"test \"{projectFile}\" -c Release --no-build --filter \"{filter}\" --list-tests")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start dotnet test process");

        var listOutput = proc.StandardOutput.ReadToEnd();
        var errorOutput = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Failed to list tests for filter '{filter}': {errorOutput}");

        return listOutput
            .Split('\n')
            .Count(line => line.StartsWith("    ", StringComparison.Ordinal));
    }

    private static void RunDotNetTest(AbsolutePath projectFile, string? filter, string baseArgs)
    {
        var filterArgs = string.IsNullOrWhiteSpace(filter) ? string.Empty : $"--filter \"{filter}\" ";
        DotNetTasks.DotNetTest(_ => _
            .SetProcessAdditionalArguments(filterArgs + baseArgs)
            .SetProjectFile(projectFile)
            .SetConfiguration("Release"));
    }

    private string? NormalizeReleaseNotes(string? releaseNotes)
    {
        return releaseNotes?
            .Replace(",", "%2C")?
            .Replace(":", "%3A")?
            .Replace(";", "%3B");
    }

    class DeploymentAppSpec
    {
        public required string AppId { get; set; }
        public required string ProjectName { get; set; }
        public required string ProjectTestName { get; set; }
    }

    private sealed record TestCategoryRun(string Category, int TestCount);
}
