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
using NukeBuildHelpers.RunContext.Extensions;
using Microsoft.Build.Logging;
using static Nuke.Common.Tools.NSwag.NSwagTasks;

class Build : BaseNukeBuildHelpers
{
    public override string[] EnvironmentBranches { get; } = ["master", "prerelease"];

    public override string MainEnvironmentBranch { get; } = "master";

    private readonly string gitRepoName = "NetConduit";
    private readonly string gitUsername = "Kiryuumaru";
    private readonly string appId = "net_conduit";
    private readonly string executableName = "net_conduit";
    private readonly string assemblyName = "netc";

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
            Console.WriteLine("Cleaning " + (RootDirectory / "out").ToString());
            (RootDirectory / "out").DeleteDirectory();
        });

    public BuildEntry NetConduitBuild => _ => _
        .AppId(appId)
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
                    string projectVersion = "0.0.0";
                    if (context.TryGetVersionedContext(out var versionedContext))
                    {
                        projectVersion = versionedContext.AppVersion.Version.WithoutMetadata().ToString();
                    }
                    var outAssetPath = GetReleaseArchivePath(runtime, arch);
                    var archivePath = outAssetPath.Parent / outAssetPath.NameWithoutExtension;
                    var outPath = archivePath / outAssetPath.NameWithoutExtension;
                    var projPath = RootDirectory / "src" / "Presentation" / "Presentation.csproj";

                    DotNetTasks.DotNetBuild(_ => _
                        .SetProjectFile(projPath)
                        .SetVersion(projectVersion)
                        .SetInformationalVersion(projectVersion)
                        .SetFileVersion(projectVersion)
                        .SetAssemblyVersion(projectVersion)
                        .SetConfiguration("Release"));
                    DotNetTasks.DotNetPublish(_ => _
                        .SetProject(projPath)
                        .SetConfiguration("Release")
                        .EnableSelfContained()
                        .SetRuntime($"{runtime}-{arch.ToLowerInvariant()}")
                        .SetVersion(projectVersion)
                        .SetInformationalVersion(projectVersion)
                        .SetFileVersion(projectVersion)
                        .SetAssemblyVersion(projectVersion)
                        .EnablePublishSingleFile()
                        .SetOutput(outPath));

                    GenerateReleaseArchive(runtime, arch);
                    GenerateOneLineInstallScript(runtime, arch);
                })));

    public PublishEntry PublishAssets => _ => _
        .AppId(appId)
        .RunnerOS(RunnerOS.Ubuntu2204)
        .WorkflowId("publish")
        .DisplayName("Publish binaries")
        .ReleaseAsset(() =>
        {
            List<AbsolutePath> paths = [];
            foreach (var runtime in runtimeMatrix)
            {
                foreach (var arch in archMatrix)
                {
                    paths.Add(GetReleaseArchivePath(runtime, arch));
                    paths.Add(GetOneLineInstallScriptPath(runtime, arch));
                }
            }
            return [.. paths];
        });

    private void GenerateOneLineInstallScript(string runtime, string arch)
    {
        string scriptExt;
        string execExt;
        switch (runtime)
        {
            case "linux":
                scriptExt = ".sh";
                execExt = "";
                break;
            case "win":
                scriptExt = ".ps1";
                execExt = ".exe";
                break;
            default:
                throw new Exception($"{runtime} not supported.");
        }
        (OutputDirectory / $"installer_{arch}{scriptExt}").WriteAllText((RootDirectory / $"installer_template{scriptExt}").ReadAllText()
            .Replace("{{$username}}", gitUsername)
            .Replace("{{$repo}}", gitRepoName)
            .Replace("{{$appname}}", GetReleaseName(runtime, arch))
            .Replace("{{$appexec}}", $"{assemblyName}{execExt}")
            .Replace("{{$rootextract}}", GetReleaseName(runtime, arch))
            .Replace("{{$homepath}}", $"$env:ProgramData\\{executableName}"));
    }

    private void GenerateReleaseArchive(string runtime, string arch)
    {
        var outAssetPath = GetReleaseArchivePath(runtime, arch);
        var archivePath = outAssetPath.Parent / outAssetPath.NameWithoutExtension;
        switch (runtime)
        {
            case "linux":
                archivePath.TarGZipTo(outAssetPath);
                break;
            case "win":
                archivePath.ZipTo(outAssetPath);
                break;
            default:
                throw new Exception($"{runtime} not supported.");
        }
    }

    private AbsolutePath GetOneLineInstallScriptPath(string runtime, string arch)
    {
        return runtime switch
        {
            "linux" => OutputDirectory / $"installer_{arch}.sh",
            "win" => OutputDirectory / $"installer_{arch}.ps1",
            _ => throw new Exception($"{runtime} not supported.")
        };
    }

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
        return OutputDirectory / GetReleaseName(runtime, arch);
    }

    private string GetReleaseName(string runtime, string arch)
    {
        return $"{executableName}-{runtime}-{arch}";
    }
}
