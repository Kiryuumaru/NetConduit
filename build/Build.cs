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
using System.Linq;

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
            AppId = "net_conduit_tcp",
            ProjectName = "NetConduit.Tcp",
            ProjectTestName = "NetConduit.Tcp.IntegrationTests"
        },
        new DeploymentAppSpec
        {
            AppId = "net_conduit_websocket",
            ProjectName = "NetConduit.WebSocket",
            ProjectTestName = "NetConduit.WebSocket.IntegrationTests"
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
                DotNetTasks.DotNetBuild(_ => _
                    .SetProjectFile(RootDirectory / "tests" / spec.ProjectTestName / $"{spec.ProjectTestName}.csproj")
                    .SetConfiguration("Release"));
                DotNetTasks.DotNetTest(_ => _
                    .SetProcessAdditionalArguments(
                        "--logger \"GitHubActions;summary.includePassedTests=true;summary.includeSkippedTests=true\" " +
                        "-- " +
                        "RunConfiguration.CollectSourceInformation=true " +
                        "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencovere ")
                    .SetProjectFile(RootDirectory / "tests" / spec.ProjectTestName / $"{spec.ProjectTestName}.csproj")
                    .SetConfiguration("Release"));
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
}
