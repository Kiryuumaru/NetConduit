using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using NukeBuildHelpers;
using NukeBuildHelpers.Attributes;
using NukeBuildHelpers.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _build;

public class NetConduitWindowsX64 : AppEntry<Build>
{
    public override RunsOnType BuildRunsOn => RunsOnType.Windows2022;

    public override RunsOnType PublishRunsOn => RunsOnType.Windows2022;

    public override bool MainRelease => false;

    public override void Build()
    {
        var projPath = RootDirectory / "src" / "Presentation" / "Presentation.csproj";
        OutputDirectory.DeleteDirectory();
        DotNetTasks.DotNetClean(_ => _
            .SetProject(projPath));
        DotNetTasks.DotNetBuild(_ => _
            .SetProjectFile(projPath)
            .SetConfiguration("Release"));
        DotNetTasks.DotNetPublish(_ => _
            .SetProject(projPath)
            .SetConfiguration("Release")
            .EnableSelfContained()
            .SetRuntime("win-x64")
            .EnablePublishSingleFile()
            .SetOutput(OutputDirectory));
    }
}
