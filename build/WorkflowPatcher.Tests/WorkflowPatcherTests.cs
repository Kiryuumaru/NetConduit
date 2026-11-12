using System.Text;
using NetConduit.Build;

namespace NetConduit.Build.Tests;

/// <summary>
/// Pins the post-generation patch applied to the workflow emitted by
/// NukeBuildHelpers: every publish job must get job-level NuGet Trusted
/// Publishing permissions, a SHA-pinned <c>NuGet/login</c> step, the
/// <c>NUKE_NUGET_AUTH_TOKEN</c> environment variable on the NUKE_RUN step,
/// and a fail-closed job-level push gate.
/// </summary>
public sealed class WorkflowPatcherTests
{
    private const string JobId = "NET_CONDUIT_PUBLISH";

    private static readonly string[] ExpectedPublishJobIds =
    [
        "NET_CONDUIT_PUBLISH",
        "NET_CONDUIT_TRANSIT_DELTA_MESSAGE_PUBLISH",
        "NET_CONDUIT_TRANSIT_DUPLEX_STREAM_PUBLISH",
        "NET_CONDUIT_TRANSIT_MESSAGE_PUBLISH",
        "NET_CONDUIT_TRANSIT_STREAM_PUBLISH",
        "NET_CONDUIT_TRANSPORT_IPC_PUBLISH",
        "NET_CONDUIT_TRANSPORT_QUIC_PUBLISH",
        "NET_CONDUIT_TRANSPORT_TCP_PUBLISH",
        "NET_CONDUIT_TRANSPORT_UDP_PUBLISH",
        "NET_CONDUIT_TRANSPORT_WEBSOCKET_PUBLISH"
    ];

    private static readonly string UnpatchedWorkflow = ToLf("""
        name: Nuke CICD Pipeline
        jobs:
          PRE_SETUP:
            name: Pre Setup
            runs-on:
              labels: ubuntu-22.04
            steps:
            - id: NUKE_RUN
              name: Run Nuke PipelinePreSetup
              run: ./build.sh PipelinePreSetup
          NET_CONDUIT_PUBLISH:
            name: Publish - net_conduit (NetConduitPublish)
            runs-on: ${{ fromJson(needs.PRE_SETUP.outputs.NUKE_PRE_SETUP_NET_CONDUIT_PUBLISH_RUNS_ON) }}
            steps:
            - uses: actions/checkout@v6
              with:
                fetch-depth: 0
                persist-credentials: true
            - name: Cache Run
              uses: actions/cache@v5
              with:
                path: ./.nuke/temp/cache
                key: ${{ needs.PRE_SETUP.outputs.NUKE_PRE_SETUP_NET_CONDUIT_PUBLISH_CACHE_KEY }}
            - id: NUKE_RUN
              name: Run Nuke net_conduit_publish
              run: ${{ needs.PRE_SETUP.outputs.NUKE_PRE_SETUP_NET_CONDUIT_PUBLISH_RUN_SCRIPT }} Run --args "net_conduit_publish"
            - name: Upload common publish artifacts
              uses: actions/upload-artifact@v7
              with:
                name: publish___$common___NET_CONDUIT_PUBLISH
            needs:
            - PRE_SETUP
            if: "! failure() && ! cancelled() && needs.PRE_SETUP.outputs.NUKE_PRE_SETUP_NET_CONDUIT_PUBLISH_CONDITION == 'true' && needs.NET_CONDUIT_TEST.result == 'success' && needs.NET_CONDUIT_BUILD.result == 'success'"
            env:
              NUKE_PRE_SETUP: ${{ needs.PRE_SETUP.outputs.NUKE_PRE_SETUP }}
          POST_SETUP:
            name: Post Setup
            runs-on:
              labels: ubuntu-22.04
            steps:
            - name: Run Nuke PipelinePostSetup
              run: ./build.sh PipelinePostSetup
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN }}
          NUKE_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        """);

    private static readonly string PatchedWorkflow = ToLf("""
        name: Nuke CICD Pipeline
        jobs:
          PRE_SETUP:
            name: Pre Setup
            runs-on:
              labels: ubuntu-22.04
            steps:
            - id: NUKE_RUN
              name: Run Nuke PipelinePreSetup
              run: ./build.sh PipelinePreSetup
          NET_CONDUIT_PUBLISH:
            name: Publish - net_conduit (NetConduitPublish)
            runs-on: ${{ fromJson(needs.PRE_SETUP.outputs.NUKE_PRE_SETUP_NET_CONDUIT_PUBLISH_RUNS_ON) }}
            permissions:
              contents: read
              packages: write
              id-token: write
            steps:
            - uses: actions/checkout@v6
              with:
                fetch-depth: 0
                persist-credentials: true
            - name: Cache Run
              uses: actions/cache@v5
              with:
                path: ./.nuke/temp/cache
                key: ${{ needs.PRE_SETUP.outputs.NUKE_PRE_SETUP_NET_CONDUIT_PUBLISH_CACHE_KEY }}
            - id: nuget_login
              name: NuGet login (OIDC)
              uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
              with:
                user: Kiryuumaru
              if: github.event_name == 'push'
            - id: NUKE_RUN
              name: Run Nuke net_conduit_publish
              run: ${{ needs.PRE_SETUP.outputs.NUKE_PRE_SETUP_NET_CONDUIT_PUBLISH_RUN_SCRIPT }} Run --args "net_conduit_publish"
              env:
                NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}
            - name: Upload common publish artifacts
              uses: actions/upload-artifact@v7
              with:
                name: publish___$common___NET_CONDUIT_PUBLISH
            needs:
            - PRE_SETUP
            if: "github.event_name == 'push' && (! failure() && ! cancelled() && needs.PRE_SETUP.outputs.NUKE_PRE_SETUP_NET_CONDUIT_PUBLISH_CONDITION == 'true' && needs.NET_CONDUIT_TEST.result == 'success' && needs.NET_CONDUIT_BUILD.result == 'success')"
            env:
              NUKE_PRE_SETUP: ${{ needs.PRE_SETUP.outputs.NUKE_PRE_SETUP }}
          POST_SETUP:
            name: Post Setup
            runs-on:
              labels: ubuntu-22.04
            steps:
            - name: Run Nuke PipelinePostSetup
              run: ./build.sh PipelinePostSetup
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          NUKE_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        """);

    [Fact]
    public void Patch_AddsPermissionsLoginStepStepEnvAndJobGateToPublishJob()
    {
        var patched = PatchSingle(UnpatchedWorkflow);

        Assert.Equal(PatchedWorkflow, patched);
    }

    [Fact]
    public void Patch_AppliedTwice_ReturnsSameString()
    {
        var once = PatchSingle(UnpatchedWorkflow);

        var twice = PatchSingle(once);

        Assert.Equal(PatchedWorkflow, once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Patch_MissingPublishJob_ThrowsNamingAnchor()
    {
        var yaml = ToLf("""
            jobs:
              PRE_SETUP:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
    }

    [Fact]
    public void Patch_MissingStepsAnchor_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                name: Publish
                runs-on: ubuntu-22.04
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("steps:", exception.Message);
    }

    [Fact]
    public void Patch_MissingNukeRunStep_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - name: Run Nuke
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("NUKE_RUN", exception.Message);
    }

    [Fact]
    public void Patch_MissingRunLine_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  name: Run Nuke
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("run:", exception.Message);
        Assert.Contains("NUKE_RUN", exception.Message);
    }

    [Theory]
    [InlineData("|")]
    [InlineData(">")]
    [InlineData("|+")]
    [InlineData("|-")]
    [InlineData(">+")]
    [InlineData(">-")]
    public void Patch_BlockScalarRun_Throws(string indicator)
    {
        var yaml = ToLf($"""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  run: {indicator}
                    ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("run:", exception.Message);
        Assert.Contains("block scalar", exception.Message);
    }

    [Fact]
    public void Patch_EmptyRun_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  run:
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("run:", exception.Message);
        Assert.Contains("single-line", exception.Message);
    }

    [Theory]
    [InlineData("# only a comment")]
    [InlineData("echo hi # trailing comment")]
    public void Patch_CommentedRun_Throws(string runValue)
    {
        var yaml = ToLf($"""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  run: {runValue}
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("run:", exception.Message);
        Assert.Contains("trailing comment", exception.Message);
    }

    [Fact]
    public void Patch_RunWithContinuationLine_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
                    --flag
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("run:", exception.Message);
        Assert.Contains("continuation", exception.Message);
    }

    [Fact]
    public void Patch_RunWithBlankAndCommentBeforeContinuation_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  run: ./build.sh

                  # folded into the plain scalar below
                    --flag
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("run:", exception.Message);
        Assert.Contains("continuation", exception.Message);
    }

    [Fact]
    public void Patch_MultiLineRunsOnMapping_InsertsPermissionsBeforeSteps()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on:
                  labels: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
                - name: Upload common publish artifacts
                  uses: actions/upload-artifact@v7
            """);

        var expected = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on:
                  labels: ubuntu-22.04
                if: "github.event_name == 'push' && (! failure())"
                permissions:
                  contents: read
                  packages: write
                  id-token: write
                steps:
                - id: nuget_login
                  name: NuGet login (OIDC)
                  uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
                  with:
                    user: Kiryuumaru
                  if: github.event_name == 'push'
                - id: NUKE_RUN
                  run: ./build.sh
                  env:
                    NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}
                - name: Upload common publish artifacts
                  uses: actions/upload-artifact@v7
            """);

        Assert.Equal(expected, PatchSingle(yaml));
    }

    [Fact]
    public void Patch_FoldedScalarRunsOn_InsertsPermissionsBeforeSteps()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: >-
                  ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
                - name: Upload common publish artifacts
                  uses: actions/upload-artifact@v7
            """);

        var expected = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: >-
                  ubuntu-22.04
                if: "github.event_name == 'push' && (! failure())"
                permissions:
                  contents: read
                  packages: write
                  id-token: write
                steps:
                - id: nuget_login
                  name: NuGet login (OIDC)
                  uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
                  with:
                    user: Kiryuumaru
                  if: github.event_name == 'push'
                - id: NUKE_RUN
                  run: ./build.sh
                  env:
                    NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}
                - name: Upload common publish artifacts
                  uses: actions/upload-artifact@v7
            """);

        Assert.Equal(expected, PatchSingle(yaml));
    }

    [Fact]
    public void Patch_PartialPermissions_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                permissions:
                  contents: read
                  id-token: write
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("partial or conflicting", exception.Message);
        Assert.Contains("permissions:", exception.Message);
    }

    [Fact]
    public void Patch_ExtraPermissions_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                permissions:
                  contents: read
                  packages: write
                  id-token: write
                  actions: read
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("extra 'permissions:' entries", exception.Message);
    }

    [Fact]
    public void Patch_InlinePermissions_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                permissions: read-all
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("inline or malformed", exception.Message);
    }

    [Fact]
    public void Patch_DuplicatePermissions_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                permissions:
                  contents: read
                  packages: write
                  id-token: write
                permissions:
                  contents: read
                  packages: write
                  id-token: write
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("more than one 'permissions:' key", exception.Message);
    }

    [Fact]
    public void Patch_LegacyV1LoginStep_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: nuget_login
                  name: NuGet login (OIDC)
                  uses: NuGet/login@v1
                  with:
                    user: Kiryuumaru
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("nuget_login", exception.Message);
        Assert.Contains("does not match the expected step", exception.Message);
    }

    [Fact]
    public void Patch_PullRequestLoginIf_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: nuget_login
                  name: NuGet login (OIDC)
                  uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
                  with:
                    user: Kiryuumaru
                  if: github.event_name != 'pull_request'
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("nuget_login", exception.Message);
        Assert.Contains("does not match the expected step", exception.Message);
    }

    [Fact]
    public void Patch_DuplicateLoginStep_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: nuget_login
                  name: NuGet login (OIDC)
                  uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
                  with:
                    user: Kiryuumaru
                  if: github.event_name == 'push'
                - id: nuget_login
                  name: NuGet login (OIDC)
                  uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
                  with:
                    user: Kiryuumaru
                  if: github.event_name == 'push'
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("more than one 'nuget_login' step", exception.Message);
    }

    [Fact]
    public void Patch_LoginStepAfterNukeRun_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                permissions:
                  contents: read
                  packages: write
                  id-token: write
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
                  env:
                    NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}
                - id: nuget_login
                  name: NuGet login (OIDC)
                  uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
                  with:
                    user: Kiryuumaru
                  if: github.event_name == 'push'
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("must precede", exception.Message);
        Assert.Contains("nuget_login", exception.Message);
        Assert.Contains("NUKE_RUN", exception.Message);
    }

    [Fact]
    public void Patch_AllExpectedPublishJobs_PatchesEveryJob()
    {
        string[] jobIds =
        [
            "NET_CONDUIT_PUBLISH",
            "NET_CONDUIT_TRANSPORT_TCP_PUBLISH",
            "NET_CONDUIT_TRANSPORT_QUIC_PUBLISH"
        ];
        var yaml = WorkflowWithPublishJobs(jobIds);

        var patched = WorkflowPatcher.Patch(yaml, jobIds);

        Assert.DoesNotContain("secrets.NUGET_AUTH_TOKEN", patched);
        Assert.Equal(jobIds.Length, CountOccurrences(patched, "    permissions:"));
        Assert.Equal(jobIds.Length, CountOccurrences(patched, "    - id: nuget_login"));
        Assert.Equal(jobIds.Length, CountOccurrences(patched,
            "        NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}"));
        Assert.Equal(jobIds.Length, CountOccurrences(patched,
            "    if: \"github.event_name == 'push'"));
        Assert.Equal(jobIds.Length, CountOccurrences(patched, "    - id: NUKE_RUN"));

        foreach (var jobId in jobIds)
        {
            AssertJobPatched(patched, jobId);
        }
    }

    [Fact]
    public void Patch_ExpectedPublishJobMissing_ThrowsWithMissingDiagnostic()
    {
        string[] expected =
        [
            "NET_CONDUIT_PUBLISH",
            "NET_CONDUIT_TRANSPORT_TCP_PUBLISH",
            "NET_CONDUIT_TRANSPORT_QUIC_PUBLISH"
        ];
        var yaml = WorkflowWithPublishJobs(
            "NET_CONDUIT_PUBLISH",
            "NET_CONDUIT_TRANSPORT_TCP_PUBLISH");

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPatcher.Patch(yaml, expected));

        Assert.Contains("Missing publish jobs", exception.Message);
        Assert.Contains("NET_CONDUIT_TRANSPORT_QUIC_PUBLISH", exception.Message);
    }

    [Fact]
    public void Patch_UnexpectedPublishJob_ThrowsWithUnexpectedDiagnostic()
    {
        string[] expected =
        [
            "NET_CONDUIT_PUBLISH",
            "NET_CONDUIT_TRANSPORT_TCP_PUBLISH"
        ];
        var yaml = WorkflowWithPublishJobs(
            "NET_CONDUIT_PUBLISH",
            "NET_CONDUIT_TRANSPORT_TCP_PUBLISH",
            "NET_CONDUIT_UNEXPECTED_PUBLISH");

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPatcher.Patch(yaml, expected));

        Assert.Contains("Unexpected publish jobs", exception.Message);
        Assert.Contains("NET_CONDUIT_UNEXPECTED_PUBLISH", exception.Message);
    }

    [Fact]
    public void Patch_DuplicatePublishJobId_Throws()
    {
        var yaml = WorkflowWithPublishJobs(JobId, JobId);

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPatcher.Patch(yaml, [JobId]));

        Assert.Contains("Duplicate top-level job IDs", exception.Message);
        Assert.Contains(JobId, exception.Message);
    }

    [Fact]
    public void Patch_ExpectedPublishJobOrder_DoesNotMatter()
    {
        string[] jobIds =
        [
            "NET_CONDUIT_PUBLISH",
            "NET_CONDUIT_TRANSPORT_TCP_PUBLISH",
            "NET_CONDUIT_TRANSPORT_QUIC_PUBLISH"
        ];
        var yaml = WorkflowWithPublishJobs(jobIds);

        var normal = WorkflowPatcher.Patch(yaml, jobIds);
        var reversed = WorkflowPatcher.Patch(yaml, jobIds.Reverse());

        Assert.Equal(normal, reversed);
    }

    [Fact]
    public void Patch_MultiplePublishJobs_IsIdempotent()
    {
        string[] jobIds =
        [
            "NET_CONDUIT_PUBLISH",
            "NET_CONDUIT_TRANSPORT_TCP_PUBLISH",
            "NET_CONDUIT_TRANSPORT_QUIC_PUBLISH"
        ];
        var yaml = WorkflowWithPublishJobs(jobIds);

        var once = WorkflowPatcher.Patch(yaml, jobIds);
        var twice = WorkflowPatcher.Patch(once, jobIds);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Patch_SecondPublishJobMalformed_DiagnosticNamesSecondJob()
    {
        const string FirstJob = "NET_CONDUIT_PUBLISH";
        const string SecondJob = "NET_CONDUIT_TRANSPORT_TCP_PUBLISH";
        var yaml = ToLf($"""
            jobs:
              {FirstJob}:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
              {SecondJob}:
                runs-on: ubuntu-22.04
                if: "! failure()"
                permissions:
                  contents: read
                  id-token: write
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPatcher.Patch(yaml, [FirstJob, SecondJob]));

        Assert.Contains(SecondJob, exception.Message);
        Assert.Contains("partial or conflicting", exception.Message);
    }

    [Fact]
    public void ExpectedPublishJobs_IsExactlyTheTenNetConduitPublishJobs()
    {
        Assert.Equal(10, WorkflowPatcher.ExpectedPublishJobs.Count);
        Assert.Equal(
            ExpectedPublishJobIds.OrderBy(id => id, StringComparer.Ordinal),
            WorkflowPatcher.ExpectedPublishJobs.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Patch_ExpectedSetWithNonPublishJobId_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPatcher.Patch(
                WorkflowWithPublishJobs(JobId),
                ["NET_CONDUIT_NOT_A_PUBLISH_JOB"]));

        Assert.Contains("does not end with 'PUBLISH'", exception.Message);
    }

    [Fact]
    public void Patch_DuplicateExpectedJobIds_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPatcher.Patch(
                WorkflowWithPublishJobs(JobId),
                [JobId, JobId]));

        Assert.Contains("duplicate IDs", exception.Message);
    }

    [Fact]
    public void Patch_UnexpectedPublishJobName_Throws()
    {
        var yaml = ToLf("""
            jobs:
              OTHERPUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("OTHERPUBLISH", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
    }

    [Fact]
    public void Patch_RemovesLegacyWorkflowLevelToken()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            env:
              GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
              NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN }}
              NUKE_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
            """);

        var patched = PatchSingle(yaml);

        Assert.DoesNotContain("secrets.NUGET_AUTH_TOKEN", patched);
        Assert.Contains(
            "env:\n  GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}\n  NUKE_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
            patched);
    }

    [Fact]
    public void Patch_LegacyTokenTopLevelFallback_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            env:
              GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
              NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN || '' }}
              NUKE_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("legacy", exception.Message);
        Assert.Contains("NUKE_NUGET_AUTH_TOKEN", exception.Message);
    }

    [Theory]
    [InlineData("NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN }}")]
    [InlineData("NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN || '' }}")]
    public void Patch_LegacyTokenAtJobLevel_Throws(string legacyLine)
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                env:
                  __LEGACY__
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """).Replace("__LEGACY__", legacyLine, StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("legacy", exception.Message);
        Assert.Contains("NUKE_NUGET_AUTH_TOKEN", exception.Message);
    }

    [Fact]
    public void Patch_LegacyTokenAtStepLevel_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - name: Cache Run
                  uses: actions/cache@v5
                  env:
                    NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN }}
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("legacy", exception.Message);
        Assert.Contains("NUKE_NUGET_AUTH_TOKEN", exception.Message);
    }

    [Theory]
    [InlineData("if: \"! failure()\"", "if: \"github.event_name == 'push' && (! failure())\"")]
    [InlineData("if: ! failure()", "if: \"github.event_name == 'push' && (! failure())\"")]
    [InlineData(
        "if: '! failure() && needs.PRE_SETUP.result == ''success'''",
        "if: \"github.event_name == 'push' && (! failure() && needs.PRE_SETUP.result == 'success')\"")]
    [InlineData(
        "if: needs.PRE_SETUP.result == 'success'",
        "if: \"github.event_name == 'push' && (needs.PRE_SETUP.result == 'success')\"")]
    [InlineData(
        "if: contains(github.ref, \"release\")",
        "if: 'github.event_name == ''push'' && (contains(github.ref, \"release\"))'")]
    [InlineData("if: ${{ ! failure() }}", "if: ${{ github.event_name == 'push' && (! failure()) }}")]
    [InlineData("if: \"${{ ! failure() }}\"", "if: ${{ github.event_name == 'push' && (! failure()) }}")]
    public void Patch_RewritableJobIf_PrependsPushGate(string ifLine, string expectedIfLine)
    {
        var patched = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = patched.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal($"    {expectedIfLine}", jobIf);
    }

    [Theory]
    [InlineData("if: \"! failure()) || (true\"")]
    [InlineData("if: \"! failure()) || (needs.PRE_SETUP.result == 'success'\"")]
    [InlineData("if: \"${{ ! failure()) || (true }}\"")]
    [InlineData("if: '! failure()) || (\"true\")'")]
    public void Patch_RewriteWithEarlyClosingParen_Throws(string ifLine)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf(ifLine)));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
        Assert.Contains("cannot be rewritten safely", exception.Message);
        Assert.Contains("balanced", exception.Message);
    }

    [Fact]
    public void Patch_RewriteWithOrInsideWrap_IsGatedAndIdempotent()
    {
        const string ifLine = "if: \"! failure() || true\"";

        var once = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = once.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal("    if: \"github.event_name == 'push' && (! failure() || true)\"", jobIf);

        var twice = PatchSingle(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Patch_RewriteSingleQuotedWithDoubleQuote_IsGated()
    {
        const string ifLine = "if: '! failure() || \"true\"'";

        var patched = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = patched.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal("    if: 'github.event_name == ''push'' && (! failure() || \"true\")'", jobIf);
    }

    [Fact]
    public void Patch_RewriteExpressionForm_IsGated()
    {
        const string ifLine = "if: ${{ ! failure() && needs.PRE_SETUP.result == 'success' }}";

        var patched = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = patched.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal(
            "    if: ${{ github.event_name == 'push' && (! failure() && needs.PRE_SETUP.result == 'success') }}",
            jobIf);
    }

    [Theory]
    [InlineData("if: ${{ ! failure() }} trailing")]
    [InlineData("if: \"prefix ${{ ! failure() }}\"")]
    [InlineData("if: ${{ ! failure() }} ${{ true }}")]
    [InlineData("if: \"! failure() }}\"")]
    [InlineData("if: ${{ ! failure()")]
    [InlineData("if: ${{ github.event_name == 'push' && (! failure())")]
    [InlineData("if: ${{ github.event_name == 'push' && (! failure()) }} || true")]
    [InlineData("if: ${{ github.event_name == 'push' && (! failure()) }} trailing")]
    public void Patch_JobIfWithEmbeddedOrSurroundingExpressionText_Throws(string ifLine)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf(ifLine)));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
        Assert.Contains("embedded or surrounding", exception.Message);
        Assert.Contains("not safely rewritable", exception.Message);
    }

    [Fact]
    public void Patch_RewriteSimpleExpressionForm_IsGatedAndIdempotent()
    {
        const string ifLine = "if: ${{ ! failure() }}";

        var once = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = once.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal("    if: ${{ github.event_name == 'push' && (! failure()) }}", jobIf);

        var twice = PatchSingle(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Patch_RewriteQuotedSimpleExpressionForm_IsGatedAndIdempotent()
    {
        const string ifLine = "if: \"${{ ! failure() }}\"";

        var once = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = once.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal("    if: ${{ github.event_name == 'push' && (! failure()) }}", jobIf);

        var twice = PatchSingle(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Patch_RewriteExpressionFormWithWhitespaceBeforeClose_IsGated()
    {
        const string ifLine = "if: ${{ ! failure()   }}";

        var patched = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = patched.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal("    if: ${{ github.event_name == 'push' && (! failure()) }}", jobIf);
    }

    [Fact]
    public void Patch_AlreadyGatedExpressionJobIfWithNestedBalancedExpression_IsNotDoubleWrapped()
    {
        const string ifLine =
            "if: ${{ github.event_name == 'push' && ((! failure()) || (needs.PRE_SETUP.result == 'success')) }}";

        var patched = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = patched.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal($"    {ifLine}", jobIf);
    }

    [Fact]
    public void Patch_RePatchWithWeakenedGate_ThrowsInsteadOfSilentNoOp()
    {
        var mutated = string.Join('\n', PatchedWorkflow.Split('\n').Select(line =>
            line.StartsWith("    if:", StringComparison.Ordinal)
                ? "    if: \"github.event_name == 'push' && (! failure()) || true\""
                : line));

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(mutated));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
        Assert.Contains("push gate", exception.Message);
        Assert.Contains("balanced", exception.Message);
    }

    [Theory]
    [InlineData("if: \"github.event_name == 'push' && (! failure())\"")]
    [InlineData("if: github.event_name == 'push' && (! failure())")]
    [InlineData("if: 'github.event_name == ''push'' && (! failure())'")]
    [InlineData("if: ${{ github.event_name == 'push' && (! failure()) }}")]
    public void Patch_AlreadyGatedJobIf_IsNotDoubleWrapped(string ifLine)
    {
        var patched = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = patched.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal($"    {ifLine}", jobIf);
    }

    [Theory]
    [InlineData("if: \"github.event_name == 'push' && (! failure()) || true\"")]
    [InlineData("if: \"github.event_name == 'push' && (! failure()) || github.event_name == 'pull_request'\"")]
    [InlineData("if: \"github.event_name == 'push' && (! failure()) && github.event_name == 'pull_request'\"")]
    public void Patch_WeakenedAlreadyGatedJobIf_Throws(string ifLine)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf(ifLine)));

        Assert.Contains("starts with the push gate", exception.Message);
        Assert.Contains("balanced", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
    }

    [Theory]
    [InlineData("if: \"github.event_name == 'push' && (! failure()\"")]
    [InlineData("if: \"github.event_name == 'push' && (! failure()))\"")]
    [InlineData("if: \"github.event_name == 'push' && ()\"")]
    [InlineData("if: \"github.event_name == 'push' && (! failure()) \"")]
    public void Patch_MalformedAlreadyGatedJobIf_Throws(string ifLine)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf(ifLine)));

        Assert.Contains("starts with the push gate", exception.Message);
        Assert.Contains("balanced", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
    }

    [Fact]
    public void Patch_AlreadyGatedJobIfWithNestedBalancedExpression_IsNotDoubleWrapped()
    {
        const string ifLine =
            "if: \"github.event_name == 'push' && ((! failure()) || (needs.PRE_SETUP.result == 'success'))\"";

        var patched = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = patched.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal($"    {ifLine}", jobIf);
    }

    [Fact]
    public void Patch_AlreadyGatedExpressionJobIfWithWhitespaceBeforeClose_IsNotDoubleWrapped()
    {
        const string ifLine =
            "if: ${{ github.event_name == 'push' && (! failure())   }}";

        var patched = PatchSingle(WorkflowWithJobIf(ifLine));

        var jobIf = patched.Split('\n').Single(line => line.StartsWith("    if:"));
        Assert.Equal($"    {ifLine}", jobIf);
    }

    [Fact]
    public void Patch_EmptyJobIf_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf("if:")));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("empty", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
    }

    [Fact]
    public void Patch_CommentOnlyJobIf_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf("if: # comment")));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("only a comment", exception.Message);
    }

    [Theory]
    [InlineData("if: \"\"")]
    [InlineData("if: ''")]
    public void Patch_EmptyQuotedJobIf_Throws(string ifLine)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf(ifLine)));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("empty quoted scalar", exception.Message);
    }

    [Theory]
    [InlineData("if: \"! failure()")]
    [InlineData("if: '! failure()")]
    public void Patch_UnterminatedQuotedJobIf_Throws(string ifLine)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf(ifLine)));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("unterminated", exception.Message);
    }

    [Fact]
    public void Patch_TrailingCommentJobIf_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf("if: ! failure() # comment")));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("trailing comment", exception.Message);
    }

    [Theory]
    [InlineData("|")]
    [InlineData("|-")]
    [InlineData("|+")]
    [InlineData(">")]
    [InlineData(">-")]
    [InlineData(">+")]
    public void Patch_BlockScalarJobIf_Throws(string indicator)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf($"if: {indicator}")));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("block scalar", exception.Message);
    }

    [Fact]
    public void Patch_MissingJobIf_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("job-level 'if:'", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
    }

    [Fact]
    public void Patch_DuplicateJobIf_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                if: "! cancelled()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("more than one job-level 'if:' key", exception.Message);
    }

    [Fact]
    public void Patch_JobIfContinuationLine_Throws()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: ! failure()
                  && needs.PRE_SETUP.result == 'success'
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => PatchSingle(yaml));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("continuation", exception.Message);
    }

    [Theory]
    [InlineData("if: \"github.event_name != 'pull_request' && (! failure())\"")]
    [InlineData("if: \"github.event_name == 'push' || true\"")]
    [InlineData("if: \"github.event_name == 'push' && ! failure()\"")]
    [InlineData("if: \"(! failure()) && github.event_name == 'push'\"")]
    [InlineData("if: \"github.event_name == 'pull_request'\"")]
    [InlineData("if: ${{ github.event_name != 'pull_request' && (! failure()) }}")]
    [InlineData("if: ${{ github.event_name == 'push' }}")]
    public void Patch_ConflictingJobIfGateTerm_Throws(string ifLine)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf(ifLine)));

        Assert.Contains("conflicting", exception.Message);
        Assert.Contains("github.event_name", exception.Message);
        Assert.Contains("NET_CONDUIT_PUBLISH", exception.Message);
    }

    [Fact]
    public void Patch_EmptyExpressionJobIf_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf("if: ${{ }}")));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("expression is empty", exception.Message);
    }

    [Fact]
    public void Patch_ExpressionJobIfWithTrailingComment_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PatchSingle(WorkflowWithJobIf("if: ${{ ! failure() # comment }}")));

        Assert.Contains("if:", exception.Message);
        Assert.Contains("trailing comment", exception.Message);
    }

    [Fact]
    public void Patch_RunStepAtEndOfFile_InsertsEnvOnSeparateLine()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh

            """);

        var patched = PatchSingle(yaml);

        Assert.Contains(
            "      run: ./build.sh\n      env:\n        NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}",
            patched);
    }

    [Fact]
    public void Patch_RunStepAtEndOfFileWithoutTrailingNewline_InsertsEnvAndKeepsFileUnterminated()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var patched = PatchSingle(yaml);

        Assert.Contains(
            "      run: ./build.sh\n      env:\n        NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}",
            patched);
        Assert.False(patched.EndsWith('\n'));
    }

    [Fact]
    public void Patch_RunStepAtEofWithoutTrailingNewline_Crlf_InsertsEnvAndKeepsFileUnterminated()
    {
        var yaml = ToCrlf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
            """);

        var patched = PatchSingle(yaml);

        Assert.Contains(
            "      run: ./build.sh\r\n      env:\r\n        NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}",
            patched);
        Assert.DoesNotContain("\n", patched.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.False(patched.EndsWith('\n'));
        Assert.False(patched.EndsWith('\r'));
    }

    [Fact]
    public void Patch_ExistingEnvBlockAtEofWithoutTrailingNewline_InsertsTokenAndKeepsFileUnterminated()
    {
        var yaml = ToLf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
                  env:
                    EXISTING: value
            """);

        var patched = PatchSingle(yaml);

        Assert.Contains(
            "      env:\n        EXISTING: value\n        NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}",
            patched);
        Assert.False(patched.EndsWith('\n'));
    }

    [Fact]
    public void Patch_ExistingEnvBlockAtEofWithoutTrailingNewline_Crlf_InsertsTokenAndKeepsFileUnterminated()
    {
        var yaml = ToCrlf("""
            jobs:
              NET_CONDUIT_PUBLISH:
                runs-on: ubuntu-22.04
                if: "! failure()"
                steps:
                - id: NUKE_RUN
                  run: ./build.sh
                  env:
                    EXISTING: value
            """);

        var patched = PatchSingle(yaml);

        Assert.Contains(
            "      env:\r\n        EXISTING: value\r\n        NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}",
            patched);
        Assert.DoesNotContain("\n", patched.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.False(patched.EndsWith('\n'));
        Assert.False(patched.EndsWith('\r'));
    }

    [Fact]
    public void Patch_CrlfInput_ProducesCrlfOutputWithSameContent()
    {
        var crlf = ToCrlf(UnpatchedWorkflow);

        var patched = PatchSingle(crlf);

        Assert.Contains("\r\n", patched);
        Assert.DoesNotContain("\n", patched.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("\r", patched.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.Equal(PatchedWorkflow, patched.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void PatchFile_WritesOnlyWhenChangedAndIsIdempotentOnDisk()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "nuke-cicd.yml");

        try
        {
            File.WriteAllText(path, UnpatchedWorkflow);

            WorkflowPatcher.PatchFile(path, [JobId]);

            Assert.Equal(PatchedWorkflow, File.ReadAllText(path));

            var marker = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, marker);

            WorkflowPatcher.PatchFile(path, [JobId]);

            Assert.Equal(marker, File.GetLastWriteTimeUtc(path));
            Assert.Equal(PatchedWorkflow, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PatchFile_PreservesUtf8BomAndDoesNotRewriteUnchangedFile()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "nuke-cicd.yml");

        try
        {
            var content = Encoding.UTF8.GetBytes(UnpatchedWorkflow);
            var bytes = new byte[3 + content.Length];
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
            content.CopyTo(bytes, 3);
            File.WriteAllBytes(path, bytes);

            WorkflowPatcher.PatchFile(path, [JobId]);

            var patchedBytes = File.ReadAllBytes(path);
            Assert.True(patchedBytes.Length >= 3);
            Assert.Equal(0xEF, patchedBytes[0]);
            Assert.Equal(0xBB, patchedBytes[1]);
            Assert.Equal(0xBF, patchedBytes[2]);
            Assert.Equal(PatchedWorkflow, Encoding.UTF8.GetString(patchedBytes, 3, patchedBytes.Length - 3));

            var marker = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, marker);

            WorkflowPatcher.PatchFile(path, [JobId]);

            Assert.Equal(marker, File.GetLastWriteTimeUtc(path));
            Assert.Equal(patchedBytes, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Patch_CommittedWorkflow_IsAlreadyFullyPatched()
    {
        var path = CommittedWorkflowLocator.Find();
        if (path is null)
        {
            throw new InvalidOperationException(
                "Could not locate '.github/workflows/nuke-cicd.yml' above the test assembly directory; " +
                "the committed-workflow guard cannot run.");
        }

        var yaml = File.ReadAllText(path);

        Assert.Equal(yaml, WorkflowPatcher.Patch(yaml));
    }

    private static string PatchSingle(string yaml) =>
        WorkflowPatcher.Patch(yaml, [JobId]);

    private static string WorkflowWithPublishJobs(params string[] jobIds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("jobs:");
        foreach (var jobId in jobIds)
        {
            builder.AppendLine($"  {jobId}:");
            builder.AppendLine("    runs-on: ubuntu-22.04");
            builder.AppendLine("    if: \"! failure()\"");
            builder.AppendLine("    steps:");
            builder.AppendLine("    - id: NUKE_RUN");
            builder.AppendLine("      run: ./build.sh");
            builder.AppendLine("    - name: Upload common publish artifacts");
            builder.AppendLine("      uses: actions/upload-artifact@v7");
        }

        builder.AppendLine("env:");
        builder.AppendLine("  GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}");
        builder.AppendLine("  NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN }}");
        builder.AppendLine("  NUKE_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}");
        return ToLf(builder.ToString());
    }

    private static void AssertJobPatched(string yaml, string jobId)
    {
        var section = GetJobSection(yaml, jobId);

        Assert.Equal(1, CountOccurrences(
            section,
            "    permissions:\n      contents: read\n      packages: write\n      id-token: write"));
        Assert.Equal(1, CountOccurrences(section, "    - id: nuget_login"));
        Assert.Equal(1, CountOccurrences(
            section,
            "        NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}"));
        Assert.Equal(1, CountOccurrences(section, "    - id: NUKE_RUN"));
        Assert.Contains(
            "    - id: nuget_login\n" +
            "      name: NuGet login (OIDC)\n" +
            "      uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0\n" +
            "      with:\n" +
            "        user: Kiryuumaru\n" +
            "      if: github.event_name == 'push'",
            section);
        Assert.Contains("    if: \"github.event_name == 'push' && (! failure())\"", section);
        Assert.True(
            section.IndexOf("    - id: nuget_login", StringComparison.Ordinal) <
            section.IndexOf("    - id: NUKE_RUN", StringComparison.Ordinal),
            $"the '{jobId}' job must run NuGet/login before NUKE_RUN");
    }

    private static string GetJobSection(string yaml, string jobId)
    {
        var lines = yaml.Split('\n');
        var start = Array.FindIndex(lines, line => line == $"  {jobId}:");
        Assert.True(start >= 0, $"job '{jobId}' was not found in the patched workflow");

        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("  ", StringComparison.Ordinal) &&
                !lines[i].StartsWith("    ", StringComparison.Ordinal) &&
                lines[i].EndsWith(':'))
            {
                end = i;
                break;
            }

            if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]))
            {
                end = i;
                break;
            }
        }

        return string.Join('\n', lines[start..end]);
    }

    private static int CountOccurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static string WorkflowWithJobIf(string ifLine) => ToLf($"""
        jobs:
          NET_CONDUIT_PUBLISH:
            runs-on: ubuntu-22.04
            {ifLine}
            steps:
            - id: NUKE_RUN
              run: ./build.sh
            - name: Upload common publish artifacts
              uses: actions/upload-artifact@v7
        """);

    /// <summary>
    /// Normalizes source line endings so fixtures read identically regardless
    /// of the checkout's line-ending configuration.
    /// </summary>
    private static string ToLf(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Produces a CRLF fixture from any checkout, normalizing first so a CRLF
    /// checkout cannot yield doubled carriage returns.
    /// </summary>
    private static string ToCrlf(string value) =>
        ToLf(value).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WorkflowPatcherTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

internal static class CommittedWorkflowLocator
{
    public static string? Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", "nuke-cicd.yml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
