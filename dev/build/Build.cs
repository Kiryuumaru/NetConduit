using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities.Collections;
using NukeBuildHelpers;

class Build : BaseNukeBuildHelpers
{
    public static int Main () => Execute<Build>(x => x.Version);

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
}
