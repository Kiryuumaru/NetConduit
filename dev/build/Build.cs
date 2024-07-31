using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using NukeBuildHelpers;
using NukeBuildHelpers.Entry;
using NukeBuildHelpers.Entry.Extensions;
using NukeBuildHelpers.Runner.Abstraction;

class Build : BaseNukeBuildHelpers
{
    public override string[] EnvironmentBranches { get; } = ["master", "prerelease"];

    public override string MainEnvironmentBranch { get; } = "master";

    public static int Main() => Execute<Build>(x => x.Version);

    public Target Clean => _ => _
        .Unlisted()
        .Description("Clean all build files")
        .Executes(delegate
        {
            foreach (var projectFile in RootDirectory.GetFiles("**.csproj", 10))
            {
                if (projectFile.Name.Equals("_build.csproj"))
                {
                    continue;
                }
                var projectDir = projectFile.Parent;
                Console.WriteLine("Cleaning " + projectDir.ToString());
                (projectDir / "bin").DeleteDirectory();
                (projectDir / "obj").DeleteDirectory();
            }
            Console.WriteLine("Cleaning " + (RootDirectory / ".vs").ToString());
            (RootDirectory / ".vs").DeleteDirectory();
        });

    public BuildEntry NetConduitBuild => _ => _
        .AppId("net_conduit")
        .Matrix(["linux", "win"], (_, runtime) => _
            .RunnerOS(runtime switch
            {
                "linux" => RunnerOS.Ubuntu2204,
                "win" => RunnerOS.Windows2022,
                _ => throw new NotSupportedException()
            })
            .Matrix(["x64", "arm64"], (_, arch) => _
                .WorkflowId($"{runtime}_{arch}")
                .DisplayName($"Build {runtime}-{arch}")
                .ReleaseAsset(GetReleaseArchivePath($"{runtime}-{arch}"))
                .Execute(context =>
                {
                    BuildBinary($"{runtime}-{arch}");
                })));

    private void BuildBinary(string runtime)
    {
        var releasePath = GetReleasePath(runtime);
        var projPath = RootDirectory / "src" / "Presentation" / "Presentation.csproj";
        DotNetTasks.DotNetClean(_ => _
            .SetProject(projPath));
        DotNetTasks.DotNetBuild(_ => _
            .SetProjectFile(projPath)
            .SetConfiguration("Release"));
        DotNetTasks.DotNetPublish(_ => _
            .SetProject(projPath)
            .SetConfiguration("Release")
            .EnableSelfContained()
            .SetRuntime(runtime)
            .EnablePublishSingleFile()
            .SetOutput(releasePath / releasePath.Name));
        if (runtime.StartsWith("linux-"))
        {
            releasePath.TarGZipTo(GetReleaseArchivePath(runtime));
        }
        else if (runtime.StartsWith("win-"))
        {
            releasePath.ZipTo(GetReleaseArchivePath(runtime));
        }
        else
        {
            throw new Exception($"{runtime} not supported.");
        }
    }

    private AbsolutePath GetReleasePath(string runtime)
    {
        return OutputDirectory / ("net_conduit-" + runtime);
    }

    private AbsolutePath GetReleaseArchivePath(string runtime)
    {
        if (runtime.StartsWith("linux-"))
        {
            return GetReleasePath(runtime) + ".tar.gz";
        }
        else if (runtime.StartsWith("win-"))
        {
            return GetReleasePath(runtime) + ".zip";
        }
        else
        {
            throw new Exception($"{runtime} not supported.");
        }
    }
}
