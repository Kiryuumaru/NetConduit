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

    public BuildEntry NetConduitLinuxX64Build => _ => _
        .AppId("net_conduit")
        .RunnerOS(RunnerOS.Ubuntu2204)
        .Execute(context =>
        {
            BuildBinary("linux-x64");
        });

    public BuildEntry NetConduitLinuxArm64Build => _ => _
        .AppId("net_conduit")
        .RunnerOS(RunnerOS.Ubuntu2204)
        .Execute(context =>
        {
            BuildBinary("linux-arm64");
        });

    public BuildEntry NetConduitWindowsX64Build => _ => _
        .AppId("net_conduit")
        .RunnerOS(RunnerOS.Windows2022)
        .Execute(context =>
        {
            BuildBinary("win-x64");
        });

    public BuildEntry NetConduitWindowsArm64Build => _ => _
        .AppId("net_conduit")
        .RunnerOS(RunnerOS.Windows2022)
        .Execute(context =>
        {
            BuildBinary("win-arm64");
        });

    private void BuildBinary(string runtime)
    {
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
            .SetOutput(OutputDirectory / runtime));
    }
}
