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
using NukeBuildHelpers.Common;
using NukeBuildHelpers.Common.Attributes;
using NukeBuildHelpers.Entry;
using NukeBuildHelpers.Entry.Extensions;
using NukeBuildHelpers.Runner.Abstraction;

class Build : BaseNukeBuildHelpers
{
    public override string[] EnvironmentBranches { get; } = ["master", "prerelease"];

    public override string MainEnvironmentBranch { get; } = "master";

    private readonly string[] runtimeMatrix = ["linux", "win"];
    private readonly string[] archMatrix = ["x64", "arm64"];

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
            Console.WriteLine("Cleaning " + (RootDirectory / ".nuke" / "temp").ToString());
            (RootDirectory / ".nuke" / "temp").DeleteDirectory();
            Console.WriteLine("Cleaning " + (RootDirectory / "out").ToString());
            (RootDirectory / "out").DeleteDirectory();
        });

    public BuildEntry NetConduitBuild => _ => _
        .AppId("net_conduit")
        .Matrix(runtimeMatrix, (_, runtime) => _
            .Matrix(archMatrix, (_, arch) => _
                .WorkflowId($"build_{runtime}_{arch}")
                .DisplayName($"Build {runtime}-{arch}")
                .RunnerOS(runtime switch
                {
                    "linux" => RunnerOS.Ubuntu2204,
                    "win" => RunnerOS.Windows2022,
                    _ => throw new NotSupportedException()
                })
                .Execute(context =>
                {
                    var releasePath = GetReleasePath(runtime, arch);
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
                        .SetRuntime($"{runtime}-{arch}")
                        .EnablePublishSingleFile()
                        .SetOutput(releasePath / releasePath.Name));
                    switch (runtime)
                    {
                        case "linux":
                            releasePath.TarGZipTo(GetReleaseArchivePath(runtime, arch));
                            break;
                        case "win":
                            releasePath.ZipTo(GetReleaseArchivePath(runtime, arch));
                            break;
                        default:
                            throw new Exception($"{runtime} not supported.");
                    }
                })));

    public PublishEntry PublishAssets => _ => _
        .AppId("net_conduit")
        .RunnerOS(RunnerOS.Ubuntu2204)
        .ReleaseAsset(() =>
        {
            List<AbsolutePath> paths = [];
            foreach (var runtime in runtimeMatrix)
            {
                foreach (var arch in archMatrix)
                {
                    paths.Add(GetReleaseArchivePath(runtime, arch));
                }
            }
            return [.. paths];
        });

    private AbsolutePath GetReleaseArchivePath(string runtime, string arch)
    {
        return runtime switch
        {
            "linux" => GetReleasePath(runtime, arch) + ".tar.gz",
            "win" => GetReleasePath(runtime, arch) + ".zip",
            _ => throw new Exception($"{runtime} not supported.")
        };
    }

    private AbsolutePath GetReleasePath(string runtime, string arch)
    {
        return OutputDirectory / $"net_conduit-{runtime}-{arch}";
    }
}
