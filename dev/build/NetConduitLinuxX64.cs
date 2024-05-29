using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using NukeBuildHelpers;
using NukeBuildHelpers.Attributes;
using NukeBuildHelpers.Enums;
using NukeBuildHelpers.Models.RunContext;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _build;

public class NetConduitLinuxX64 : AppEntry<Build>
{
    public override RunsOnType BuildRunsOn => RunsOnType.Ubuntu2204;

    public override RunsOnType PublishRunsOn => RunsOnType.Ubuntu2204;

    public override void Build(AppRunContext appRunContext)
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
            .SetRuntime("linux-x64")
            .EnablePublishSingleFile()
            .SetOutput(OutputDirectory));
    }
}
