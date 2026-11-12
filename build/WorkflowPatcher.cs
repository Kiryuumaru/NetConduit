// REMOVE WHEN: NukeBuildHelpers can emit job-level `permissions`, a job-level
// PR gate, a pre-step NuGet/login (OIDC) step, and step-level env directly
// from its GitHub pipeline builder. This helper is a stopgap; see
// build/README.md for the full removal checklist.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NetConduit.Build;

/// <summary>
/// Post-generation patcher for the GitHub Actions workflow emitted by
/// NukeBuildHelpers' <c>GithubWorkflow</c> target.
/// </summary>
public static class WorkflowPatcher
{
    /// <summary>
    /// The exact set of top-level publish job IDs this repository expects.
    /// Discovery is suffix-based, but the discovered set must equal this set:
    /// a missing, renamed, or unexpected publish job fails the patch instead
    /// of silently granting OIDC permissions to the wrong job.
    /// </summary>
    public static IReadOnlyList<string> ExpectedPublishJobs { get; } =
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

    private const string NuGetUser = "Kiryuumaru";
    private const string NuGetLoginStepId = "nuget_login";
    private const string NukeRunStepId = "NUKE_RUN";
    private const string NuGetApiKeyEnv = "NUKE_NUGET_AUTH_TOKEN";
    private const string NuGetApiKeyExpression = "${{ steps.nuget_login.outputs.NUGET_API_KEY }}";

    // NuGet/login v1.2.0, pinned to an immutable commit SHA instead of a
    // floating tag.
    private const string NuGetLoginAction =
        "NuGet/login@8d196754b4036150537f80ac539e15c2f1028841";

    private const string LegacyWorkflowTokenEntry =
        "NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN }}";

    // Positive gate: the workflow triggers only on `push` and `pull_request`.
    // `push` covers branch and bump-* tag pushes and fails closed for any event
    // added later (pull_request_target, workflow_dispatch, ...).
    private const string JobGatePrefix =
        "github.event_name == 'push' && (";
    private const string ExpressionJobGatePrefix =
        "${{ github.event_name == 'push' && (";

    private static readonly string[] ExpectedPermissions =
    [
        "    permissions:",
        "      contents: read",
        "      packages: write",
        "      id-token: write"
    ];

    private static readonly string[] ExpectedLoginStep =
    [
        $"    - id: {NuGetLoginStepId}",
        "      name: NuGet login (OIDC)",
        $"      uses: {NuGetLoginAction} # v1.2.0",
        "      with:",
        $"        user: {NuGetUser}",
        "      if: github.event_name == 'push'"
    ];

    private static readonly string ExpectedStepEnvLine =
        $"        {NuGetApiKeyEnv}: {NuGetApiKeyExpression}";

    /// <summary>
    /// Returns a copy of <paramref name="yaml"/> with the NuGet Trusted
    /// Publishing additions applied to every expected publish job.
    /// </summary>
    public static string Patch(string yaml) => Patch(yaml, ExpectedPublishJobs);

    /// <summary>
    /// Returns a copy of <paramref name="yaml"/> with the NuGet Trusted
    /// Publishing additions applied to every publish job. The discovered
    /// publish-job set must equal <paramref name="expectedPublishJobs"/>.
    /// </summary>
    public static string Patch(string yaml, IEnumerable<string> expectedPublishJobs)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var expected = NormalizeExpectedJobs(expectedPublishJobs);

        var lines = SplitLines(yaml);
        var ending = DominantEnding(lines);

        // Remove the top-level legacy token before discovering jobs: a
        // top-level env block above `jobs:` would otherwise shift anchors.
        var changed = RemoveLegacyWorkflowLevelToken(lines);
        var jobs = FindPublishJobs(lines, expected);

        // Patch from the bottom of the file upwards so insertions in a later
        // job cannot invalidate an earlier job's anchors.
        for (var i = jobs.Count - 1; i >= 0; i--)
        {
            changed |= PatchJob(lines, jobs[i], ending);
        }

        if (!changed)
        {
            Verify(yaml, expected);
            return yaml;
        }

        var patched = string.Concat(lines.Select(line => line.Text + line.Ending));
        Verify(patched, expected);
        return patched;
    }

    /// <summary>
    /// Reads <paramref name="path"/>, applies <see cref="Patch(string)"/>, and
    /// writes the result back only when the content changed. A UTF-8 BOM is
    /// preserved.
    /// </summary>
    public static void PatchFile(string path) => PatchFile(path, ExpectedPublishJobs);

    /// <summary>
    /// Reads <paramref name="path"/>, applies
    /// <see cref="Patch(string, IEnumerable{string})"/>, and writes the result
    /// back only when the content changed. A UTF-8 BOM is preserved.
    /// </summary>
    public static void PatchFile(string path, IEnumerable<string> expectedPublishJobs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedPublishJobs);

        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        var yaml = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);

        var patched = Patch(yaml, expectedPublishJobs);
        if (!string.Equals(yaml, patched, StringComparison.Ordinal))
        {
            File.WriteAllText(path, patched,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom));
        }
    }

    private static string[] NormalizeExpectedJobs(IEnumerable<string> expectedPublishJobs)
    {
        ArgumentNullException.ThrowIfNull(expectedPublishJobs);

        var jobs = expectedPublishJobs.ToArray();
        if (jobs.Length == 0)
        {
            throw new InvalidOperationException("The expected publish-job set is empty.");
        }

        var duplicateIds = jobs
            .GroupBy(job => job, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(job => job, StringComparer.Ordinal)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                "The expected publish-job set contains duplicate IDs: " +
                $"{string.Join(", ", duplicateIds)}.");
        }

        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job) ||
                !Regex.IsMatch(job, @"^[A-Za-z0-9_-]+$"))
            {
                throw new InvalidOperationException(
                    $"Expected publish job ID '{job}' is not a valid top-level job ID.");
            }

            if (!job.EndsWith("PUBLISH", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected publish job ID '{job}' does not end with 'PUBLISH'; " +
                    "the patcher discovers publish jobs by that suffix.");
            }
        }

        return jobs;
    }

    private static bool PatchJob(List<Line> lines, PublishJob job, string ending)
    {
        var jobStart = job.Start;
        var jobEnd = job.End;

        var stepsIndex = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    steps:\s*$"));
        if (stepsIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not find the job-level 'steps:' anchor in the '{job.Id}' job.");
        }

        var nukeRunIndex = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    - id: NUKE_RUN\b"));
        if (nukeRunIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not find the '- id: {NukeRunStepId}' step in the '{job.Id}' job.");
        }

        var nukeRunEnd = FindIndex(lines, nukeRunIndex + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    - "));
        if (nukeRunEnd < 0)
        {
            nukeRunEnd = jobEnd;
        }

        var runIndex = FindIndex(lines, nukeRunIndex + 1, nukeRunEnd,
            line => Regex.IsMatch(line.Text, @"^      run:"));
        if (runIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not find the 'run:' line in the '{NukeRunStepId}' step of the '{job.Id}' job.");
        }

        EnsureSingleLineScalar(lines[runIndex], "run:", NukeRunStepId, job.Id);
        EnsureNoPlainScalarContinuation(lines, runIndex, nukeRunEnd, job.Id);

        var jobGate = GatePublishJobFromPullRequests(lines, jobStart, jobEnd, job.Id);

        var hasPermissions = InspectPermissions(lines, jobStart, jobEnd, job.Id);
        var hasLogin = InspectLoginStep(lines, jobStart, jobEnd, job.Id);
        var stepEnv = InspectStepEnv(lines, nukeRunIndex, nukeRunEnd, runIndex, job.Id);

        // Apply insertions from the bottom of the job upwards so earlier
        // indices remain valid.
        var changed = false;
        if (!stepEnv.Present)
        {
            if (stepEnv.HasEnvBlock)
            {
                InsertLines(lines, stepEnv.InsertIndex, [ExpectedStepEnvLine], ending);
            }
            else
            {
                InsertLines(lines, stepEnv.InsertIndex,
                    ["      env:", ExpectedStepEnvLine], ending);
            }

            changed = true;
        }

        if (!hasLogin)
        {
            InsertLines(lines, nukeRunIndex, ExpectedLoginStep, ending);
            changed = true;
        }

        if (!hasPermissions)
        {
            InsertLines(lines, stepsIndex, ExpectedPermissions, ending);
            changed = true;
        }

        if (jobGate == JobGateInspection.Applied)
        {
            changed = true;
        }

        return changed;
    }

    private static void Verify(string yaml, string[] expected)
    {
        var lines = SplitLines(yaml);
        var jobs = FindPublishJobs(lines, expected);

        foreach (var job in jobs)
        {
            VerifyJob(lines, job);
        }

        if (ContainsLegacyWorkflowLevelToken(lines))
        {
            throw new InvalidOperationException(
                "Patch verification failed: a legacy 'NUKE_NUGET_AUTH_TOKEN: " +
                "${{ secrets.NUGET_AUTH_TOKEN }}' reference is still present somewhere in the workflow " +
                "(top-level, job-level, or step-level).");
        }
    }

    private static void VerifyJob(List<Line> lines, PublishJob job)
    {
        var jobStart = job.Start;
        var jobEnd = job.End;

        var ifIndex = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    if:"));
        if (ifIndex < 0)
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{job.Id}' job has no job-level 'if:' gate.");
        }

        var ifValue = UnquoteScalar(lines[ifIndex].Text["    if:".Length..].Trim());
        var isExpressionGate = ifValue.StartsWith(ExpressionJobGatePrefix, StringComparison.Ordinal);
        if (!isExpressionGate &&
            !ifValue.StartsWith(JobGatePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{job.Id}' job does not start its job-level " +
                $"'if:' with the push gate ('{JobGatePrefix}...').");
        }

        if (!IsExactPushGate(ifValue, isExpressionGate))
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{job.Id}' job's job-level 'if:' is not " +
                "exactly the push gate applied to one balanced parenthesized expression " +
                $"('{JobGatePrefix}...'); refusing a conflicting, weakened, or misplaced gate.");
        }

        var permissionsIndex = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    permissions:"));
        if (permissionsIndex < 0 ||
            !BlockMatches(lines, permissionsIndex, ExpectedPermissions, jobEnd))
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{job.Id}' job does not contain the expected permissions block:\n{Format(ExpectedPermissions)}");
        }

        var afterPermissions = permissionsIndex + ExpectedPermissions.Length;
        if (afterPermissions < jobEnd &&
            lines[afterPermissions].Text.StartsWith("      ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{job.Id}' job has unexpected extra permissions entries.");
        }

        var loginIndex = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    - id: nuget_login\b"));
        if (loginIndex < 0)
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{NuGetLoginStepId}' step was not found in the '{job.Id}' job.");
        }

        var loginEnd = FindIndex(lines, loginIndex + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    - "));
        if (loginEnd < 0)
        {
            loginEnd = jobEnd;
        }

        while (loginEnd > loginIndex && lines[loginEnd - 1].Text.Length == 0)
        {
            loginEnd--;
        }

        if (loginEnd - loginIndex != ExpectedLoginStep.Length ||
            !BlockMatches(lines, loginIndex, ExpectedLoginStep, loginEnd))
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{NuGetLoginStepId}' step in the '{job.Id}' job does not match the expected shape:\n{Format(ExpectedLoginStep)}");
        }

        var nukeRunIndex = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    - id: NUKE_RUN\b"));
        if (nukeRunIndex < 0)
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '- id: {NukeRunStepId}' step was not found in the '{job.Id}' job.");
        }

        if (loginIndex > nukeRunIndex)
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{NuGetLoginStepId}' step must precede the '{NukeRunStepId}' step in the '{job.Id}' job.");
        }

        var nukeRunEnd = FindIndex(lines, nukeRunIndex + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    - "));
        if (nukeRunEnd < 0)
        {
            nukeRunEnd = jobEnd;
        }

        var envIndex = FindIndex(lines, nukeRunIndex + 1, nukeRunEnd,
            line => Regex.IsMatch(line.Text, @"^      env:\s*$"));
        var envKeyIndex = -1;
        if (envIndex >= 0)
        {
            var envEnd = envIndex + 1;
            while (envEnd < nukeRunEnd &&
                   (lines[envEnd].Text.Length == 0 ||
                    lines[envEnd].Text.StartsWith("        ", StringComparison.Ordinal)))
            {
                envEnd++;
            }

            envKeyIndex = FindIndex(lines, envIndex + 1, envEnd,
                line => Regex.IsMatch(line.Text, @"^        NUKE_NUGET_AUTH_TOKEN:"));
        }

        if (envKeyIndex < 0 ||
            lines[envKeyIndex].Text.TrimEnd() != ExpectedStepEnvLine)
        {
            throw new InvalidOperationException(
                $"Patch verification failed: the '{NukeRunStepId}' step in the '{job.Id}' job does not attach " +
                $"'{NuGetApiKeyEnv}: {NuGetApiKeyExpression}' to its own 'env:' block.");
        }
    }

    private static bool InspectPermissions(
        List<Line> lines,
        int jobStart,
        int jobEnd,
        string jobId)
    {
        var index = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    permissions:"));
        if (index < 0)
        {
            return false;
        }

        if (FindIndex(lines, index + 1, jobEnd,
                line => Regex.IsMatch(line.Text, @"^    permissions:")) >= 0)
        {
            throw new InvalidOperationException(
                $"The '{jobId}' job contains more than one 'permissions:' key; refusing to guess which one is authoritative.");
        }

        if (lines[index].Text.TrimEnd() != ExpectedPermissions[0])
        {
            throw new InvalidOperationException(
                $"The '{jobId}' job has an inline or malformed 'permissions:' value; expected exactly:\n{Format(ExpectedPermissions)}");
        }

        if (!BlockMatches(lines, index, ExpectedPermissions, jobEnd))
        {
            throw new InvalidOperationException(
                $"The '{jobId}' job has a partial or conflicting 'permissions:' block; expected exactly:\n{Format(ExpectedPermissions)}");
        }

        var afterPermissions = index + ExpectedPermissions.Length;
        if (afterPermissions < jobEnd &&
            lines[afterPermissions].Text.StartsWith("      ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The '{jobId}' job has extra 'permissions:' entries; expected exactly:\n{Format(ExpectedPermissions)}");
        }

        return true;
    }

    private static bool InspectLoginStep(
        List<Line> lines,
        int jobStart,
        int jobEnd,
        string jobId)
    {
        var first = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    - id: nuget_login\b"));
        if (first < 0)
        {
            return false;
        }

        if (FindIndex(lines, first + 1, jobEnd,
                line => Regex.IsMatch(line.Text, @"^    - id: nuget_login\b")) >= 0)
        {
            throw new InvalidOperationException(
                $"The '{jobId}' job contains more than one '{NuGetLoginStepId}' step; refusing to guess which one to keep.");
        }

        var end = FindIndex(lines, first + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    - "));
        if (end < 0)
        {
            end = jobEnd;
        }

        while (end > first && lines[end - 1].Text.Length == 0)
        {
            end--;
        }

        if (end - first != ExpectedLoginStep.Length ||
            !BlockMatches(lines, first, ExpectedLoginStep, end))
        {
            throw new InvalidOperationException(
                $"The '{jobId}' job already has a '{NuGetLoginStepId}' step that does not match the expected step; " +
                $"refusing to duplicate or silently accept it. Expected exactly:\n{Format(ExpectedLoginStep)}");
        }

        return true;
    }

    private static StepEnvInspection InspectStepEnv(
        List<Line> lines,
        int nukeRunIndex,
        int nukeRunEnd,
        int runIndex,
        string jobId)
    {
        var envIndex = FindIndex(lines, nukeRunIndex + 1, nukeRunEnd,
            line => Regex.IsMatch(line.Text, @"^      env:"));
        if (envIndex < 0)
        {
            return new StepEnvInspection(Present: false, HasEnvBlock: false, InsertIndex: runIndex + 1);
        }

        if (!Regex.IsMatch(lines[envIndex].Text, @"^      env:\s*$"))
        {
            throw new InvalidOperationException(
                $"The '{NukeRunStepId}' step in the '{jobId}' job has an inline 'env:' value; expected a block mapping.");
        }

        var envEnd = envIndex + 1;
        while (envEnd < nukeRunEnd &&
               (lines[envEnd].Text.Length == 0 ||
                lines[envEnd].Text.StartsWith("        ", StringComparison.Ordinal)))
        {
            envEnd++;
        }

        var keyIndex = FindIndex(lines, envIndex + 1, envEnd,
            line => Regex.IsMatch(line.Text, @"^        NUKE_NUGET_AUTH_TOKEN:"));
        if (keyIndex < 0)
        {
            return new StepEnvInspection(Present: false, HasEnvBlock: true, InsertIndex: envEnd);
        }

        if (lines[keyIndex].Text.TrimEnd() != ExpectedStepEnvLine)
        {
            throw new InvalidOperationException(
                $"The '{NukeRunStepId}' step in the '{jobId}' job already sets '{NuGetApiKeyEnv}' to a different value; " +
                $"expected '{ExpectedStepEnvLine.Trim()}'.");
        }

        return new StepEnvInspection(Present: true, HasEnvBlock: true, InsertIndex: -1);
    }

    private static void EnsureSingleLineScalar(
        Line line,
        string key,
        string stepId,
        string jobId)
    {
        var prefix = $"      {key}";
        if (!line.Text.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Could not find the '{key}' line in the '{stepId}' step of the '{jobId}' job.");
        }

        var value = line.Text[prefix.Length..].Trim();
        if (value.Length == 0 ||
            value[0] is '|' or '>' ||
            value.StartsWith('#') ||
            value.Contains(" #", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The '{key}' value in the '{stepId}' step of the '{jobId}' job is not a single-line plain scalar " +
                "(block scalar or trailing comment detected); refusing to insert 'env:' after it.");
        }
    }

    /// <summary>
    /// Rejects a plain multi-line scalar: a non-blank, non-comment line that is
    /// indented more than the step keys (six spaces) continues the <c>run:</c>
    /// value, so inserting <c>env:</c> after the first line would corrupt it.
    /// </summary>
    private static void EnsureNoPlainScalarContinuation(
        List<Line> lines,
        int runIndex,
        int nukeRunEnd,
        string jobId)
    {
        for (var i = runIndex + 1; i < nukeRunEnd; i++)
        {
            var text = lines[i].Text;
            var trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = 0;
            while (indent < text.Length && text[indent] == ' ')
            {
                indent++;
            }

            if (indent > 6)
            {
                throw new InvalidOperationException(
                    $"The 'run:' value in the 'NUKE_RUN' step of the '{jobId}' job is not a single-line plain scalar " +
                    "(a more-indented continuation line was detected); refusing to insert 'env:' into the scalar.");
            }

            return;
        }
    }

    private enum JobGateInspection
    {
        /// <summary>
        /// The job-level <c>if:</c> is exactly the push gate applied to one
        /// balanced parenthesized expression.
        /// </summary>
        AlreadyGated,

        /// <summary>The job-level <c>if:</c> was rewritten to start with the push gate.</summary>
        Applied
    }

    /// <summary>
    /// Rewrites the publish job's single-line <c>if:</c> so its first
    /// top-level conjunct is <c>github.event_name == 'push'</c>. The job gate
    /// is the security boundary: pull requests and any future non-push event
    /// skip the job (and its write permissions) entirely. Every shape that
    /// cannot be gated safely throws instead of leaving the job ungated.
    /// </summary>
    private static JobGateInspection GatePublishJobFromPullRequests(
        List<Line> lines,
        int jobStart,
        int jobEnd,
        string jobId)
    {
        var ifIndex = FindIndex(lines, jobStart + 1, jobEnd,
            line => Regex.IsMatch(line.Text, @"^    if:"));
        if (ifIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not find the job-level 'if:' anchor in the '{jobId}' job; " +
                "refusing to emit an ungated publish job.");
        }

        if (FindIndex(lines, ifIndex + 1, jobEnd,
                line => Regex.IsMatch(line.Text, @"^    if:")) >= 0)
        {
            throw new InvalidOperationException(
                $"The '{jobId}' job contains more than one job-level 'if:' key; " +
                "refusing to guess which one is authoritative.");
        }

        EnsureNoIfContinuation(lines, ifIndex, jobEnd, jobId);

        const string Prefix = "    if:";
        var line = lines[ifIndex];
        var value = line.Text[Prefix.Length..].Trim();

        if (value.Length == 0)
        {
            throw UngateableIf(jobId, "the value is empty");
        }

        if (value[0] is '|' or '>')
        {
            throw UngateableIf(jobId, "the value is a block scalar");
        }

        if (value[0] == '#')
        {
            throw UngateableIf(jobId, "the value is missing (only a comment is present)");
        }

        string raw;
        if (value[0] == '"')
        {
            if (value.Length < 2 || value[^1] != '"')
            {
                throw UngateableIf(jobId, "the double-quoted value is unterminated");
            }

            raw = value[1..^1];
        }
        else if (value[0] == '\'')
        {
            if (value.Length < 2 || value[^1] != '\'')
            {
                throw UngateableIf(jobId, "the single-quoted value is unterminated");
            }

            raw = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        else
        {
            if (value.Contains(" #", StringComparison.Ordinal))
            {
                throw UngateableIf(jobId, "the value has a trailing comment");
            }

            raw = value;
        }

        if (raw.Trim().Length == 0)
        {
            throw UngateableIf(jobId, "the value is an empty quoted scalar");
        }

        if (raw.Contains("${{", StringComparison.Ordinal) ||
            raw.Contains("}}", StringComparison.Ordinal))
        {
            var expression = raw.Trim();
            if (!IsSingleExpression(expression))
            {
                throw new InvalidOperationException(
                    $"The job-level 'if:' in the '{jobId}' job contains embedded or " +
                    "surrounding '${{ ... }}' expression text; refusing to rewrite it because " +
                    "it is not safely rewritable.");
            }

            raw = expression;
        }

        if (raw.StartsWith(JobGatePrefix, StringComparison.Ordinal) ||
            raw.StartsWith(ExpressionJobGatePrefix, StringComparison.Ordinal))
        {
            if (IsExactPushGate(raw, raw.StartsWith(ExpressionJobGatePrefix, StringComparison.Ordinal)))
            {
                return JobGateInspection.AlreadyGated;
            }

            throw new InvalidOperationException(
                $"The job-level 'if:' in the '{jobId}' job starts with the push gate " +
                $"('{JobGatePrefix}...') but is not exactly that gate applied to one balanced " +
                "parenthesized expression; refusing to accept a conflicting, weakened, or " +
                "misplaced gate.");
        }

        if (raw.Contains("github.event_name", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The job-level 'if:' in the '{jobId}' job contains a conflicting " +
                "'github.event_name' condition that is not the expected push gate " +
                $"('{JobGatePrefix}...'); refusing to accept a weakened or misplaced gate.");
        }

        string gated;
        bool expressionForm;
        if (raw.StartsWith("${{", StringComparison.Ordinal) &&
            raw.EndsWith("}}", StringComparison.Ordinal))
        {
            var inner = raw[3..^2].Trim();
            if (inner.Length == 0)
            {
                throw UngateableIf(jobId, "the ${{ }} expression is empty");
            }

            if (inner.Contains(" #", StringComparison.Ordinal))
            {
                throw UngateableIf(jobId, "the ${{ }} expression has a trailing comment");
            }

            gated = $"${{{{ github.event_name == 'push' && ({inner}) }}}}";
            expressionForm = true;
        }
        else if (!raw.Contains('"') && !raw.Contains('\\'))
        {
            gated = $"\"github.event_name == 'push' && ({raw})\"";
            expressionForm = false;
        }
        else
        {
            var escaped = raw.Replace("'", "''", StringComparison.Ordinal);
            gated = $"'github.event_name == ''push'' && ({escaped})'";
            expressionForm = false;
        }

        var emitted = expressionForm
            ? gated
            : gated[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (!IsExactPushGate(emitted, expressionForm))
        {
            throw new InvalidOperationException(
                $"The job-level 'if:' in the '{jobId}' job cannot be rewritten safely: the " +
                "emitted value is not exactly the push gate applied to one balanced parenthesized " +
                "expression; refusing to emit it.");
        }

        lines[ifIndex] = new Line($"{Prefix} {gated}", line.Ending);
        return JobGateInspection.Applied;
    }

    /// <summary>
    /// Validates that <paramref name="value"/> is exactly one
    /// <c>${{ ... }}</c> expression: it starts with <c>${{</c>, the first
    /// closing <c>}}</c> after the opening is the final two characters
    /// (whitespace before it is allowed), and no second opening appears.
    /// </summary>
    private static bool IsSingleExpression(string value)
    {
        if (!value.StartsWith("${{", StringComparison.Ordinal) ||
            !value.EndsWith("}}", StringComparison.Ordinal))
        {
            return false;
        }

        if (value.IndexOf("${{", 3, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var closeIndex = value.IndexOf("}}", 3, StringComparison.Ordinal);
        return closeIndex >= 0 && closeIndex == value.Length - 2;
    }

    /// <summary>
    /// Validates that <paramref name="raw"/> is exactly the push gate applied
    /// to one balanced parenthesized expression. The opening parenthesis from
    /// the gate prefix must close only at the end of the wrapped expression,
    /// so no top-level <c>||</c> or extra <c>github.event_name</c> term can
    /// sit outside it.
    /// </summary>
    private static bool IsExactPushGate(string raw, bool expressionForm)
    {
        var prefix = expressionForm ? ExpressionJobGatePrefix : JobGatePrefix;
        if (raw.Length <= prefix.Length)
        {
            return false;
        }

        var openIndex = prefix.Length - 1;
        if (raw[openIndex] != '(')
        {
            return false;
        }

        var depth = 0;
        var closeIndex = -1;
        for (var i = openIndex; i < raw.Length; i++)
        {
            if (raw[i] == '(')
            {
                depth++;
            }
            else if (raw[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    closeIndex = i;
                    break;
                }
            }
        }

        if (closeIndex < 0 || raw[(openIndex + 1)..closeIndex].Trim().Length == 0)
        {
            return false;
        }

        var tail = raw[(closeIndex + 1)..];
        return expressionForm
            ? tail.TrimStart() == "}}"
            : tail.Length == 0;
    }

    /// <summary>
    /// Rejects a plain multi-line <c>if:</c>: a non-blank, non-comment line
    /// indented more than the job keys (four spaces) continues the value.
    /// </summary>
    private static void EnsureNoIfContinuation(
        List<Line> lines,
        int ifIndex,
        int jobEnd,
        string jobId)
    {
        for (var i = ifIndex + 1; i < jobEnd; i++)
        {
            var text = lines[i].Text;
            var trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = 0;
            while (indent < text.Length && text[indent] == ' ')
            {
                indent++;
            }

            if (indent > 4)
            {
                throw new InvalidOperationException(
                    $"The job-level 'if:' in the '{jobId}' job has a more-indented continuation line; " +
                    "refusing to rewrite a plain multi-line condition.");
            }

            return;
        }
    }

    private static InvalidOperationException UngateableIf(string jobId, string reason)
    {
        return new InvalidOperationException(
            $"The job-level 'if:' in the '{jobId}' job cannot be gated safely: {reason}. " +
            "Refusing to emit an ungated publish job.");
    }

    private static bool BlockMatches(
        List<Line> lines,
        int start,
        string[] expected,
        int limit)
    {
        if (start + expected.Length > limit)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (lines[start + i].Text.TrimEnd() != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool RemoveLegacyWorkflowLevelToken(List<Line> lines)
    {
        var envIndex = FindTopLevelEnv(lines);
        if (envIndex < 0)
        {
            return false;
        }

        var envEnd = TopLevelBlockEnd(lines, envIndex);
        var removed = false;
        for (var i = envEnd - 1; i > envIndex; i--)
        {
            if (lines[i].Text.Trim() == LegacyWorkflowTokenEntry)
            {
                lines.RemoveAt(i);
                removed = true;
            }
        }

        return removed;
    }

    private static bool ContainsLegacyWorkflowLevelToken(List<Line> lines)
    {
        return lines.Any(line =>
            line.Text.Contains(NuGetApiKeyEnv, StringComparison.Ordinal) &&
            line.Text.Contains("secrets.NUGET_AUTH_TOKEN", StringComparison.Ordinal));
    }

    private static int FindTopLevelEnv(List<Line> lines)
    {
        return FindIndex(lines, 0, lines.Count,
            line => Regex.IsMatch(line.Text, @"^env:\s*$"));
    }

    private static int TopLevelBlockEnd(List<Line> lines, int start)
    {
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (lines[i].Text.Length > 0 && !char.IsWhiteSpace(lines[i].Text[0]))
            {
                return i;
            }
        }

        return lines.Count;
    }

    private static List<PublishJob> FindPublishJobs(List<Line> lines, string[] expected)
    {
        var jobsIndex = FindIndex(lines, 0, lines.Count,
            line => Regex.IsMatch(line.Text, @"^jobs:\s*$"));
        if (jobsIndex < 0)
        {
            throw new InvalidOperationException(
                "Could not find the top-level 'jobs:' key in the generated workflow.");
        }

        var jobsEnd = lines.Count;
        for (var i = jobsIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].Text.Length > 0 && !char.IsWhiteSpace(lines[i].Text[0]))
            {
                jobsEnd = i;
                break;
            }
        }

        var jobs = new List<(int Index, string Id)>();
        for (var i = jobsIndex + 1; i < jobsEnd; i++)
        {
            var match = Regex.Match(lines[i].Text, @"^  ([A-Za-z0-9_-]+):\s*$");
            if (match.Success)
            {
                jobs.Add((i, match.Groups[1].Value));
            }
        }

        var duplicateIds = jobs
            .GroupBy(job => job.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(job => job, StringComparer.Ordinal)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate top-level job IDs found in the generated workflow " +
                $"({string.Join(", ", duplicateIds)}); refusing to patch.");
        }

        var publishJobs = jobs
            .Where(job => job.Id.EndsWith("PUBLISH", StringComparison.Ordinal))
            .ToList();

        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
        var discoveredSet = new HashSet<string>(
            publishJobs.Select(job => job.Id), StringComparer.Ordinal);
        var missing = expectedSet
            .Except(discoveredSet, StringComparer.Ordinal)
            .OrderBy(job => job, StringComparer.Ordinal)
            .ToArray();
        var unexpected = discoveredSet
            .Except(expectedSet, StringComparer.Ordinal)
            .OrderBy(job => job, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0 || unexpected.Length > 0)
        {
            var parts = new List<string>();
            if (missing.Length > 0)
            {
                parts.Add($"Missing publish jobs: {string.Join(", ", missing)}.");
            }

            if (unexpected.Length > 0)
            {
                parts.Add($"Unexpected publish jobs: {string.Join(", ", unexpected)}.");
            }

            parts.Add(
                "Expected exactly: " +
                $"{string.Join(", ", expectedSet.OrderBy(job => job, StringComparer.Ordinal))}.");
            throw new InvalidOperationException(
                "The generated workflow's publish jobs do not match the expected set. " +
                string.Join(" ", parts));
        }

        var result = new List<PublishJob>(publishJobs.Count);
        foreach (var publishJob in publishJobs)
        {
            var end = jobsEnd;
            foreach (var job in jobs)
            {
                if (job.Index > publishJob.Index)
                {
                    end = job.Index;
                    break;
                }
            }

            result.Add(new PublishJob(publishJob.Id, publishJob.Index, end));
        }

        return result;
    }

    private static int FindIndex(
        List<Line> lines,
        int start,
        int end,
        Func<Line, bool> predicate)
    {
        for (var i = start; i < end; i++)
        {
            if (predicate(lines[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Format(IEnumerable<string> block)
    {
        return string.Join(Environment.NewLine, block);
    }

    /// <summary>
    /// Strips one layer of YAML quotes from a single-line scalar value.
    /// </summary>
    private static string UnquoteScalar(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return value;
    }

    private static string DominantEnding(List<Line> lines)
    {
        string? best = null;
        var bestCount = 0;
        foreach (var candidate in new[] { "\n", "\r\n", "\r" })
        {
            var count = lines.Count(line => line.Ending == candidate);
            if (count > bestCount)
            {
                best = candidate;
                bestCount = count;
            }
        }

        return best ?? "\n";
    }

    /// <summary>
    /// Inserts <paramref name="texts"/> at <paramref name="index"/>, first
    /// terminating the preceding line when it is the last line of a file with
    /// no trailing newline. A file that ended without a newline keeps that
    /// shape: the final inserted line has no ending when the insertion reaches
    /// EOF.
    /// </summary>
    private static void InsertLines(
        List<Line> lines,
        int index,
        string[] texts,
        string ending)
    {
        var eofWithoutNewline = index == lines.Count
            && lines.Count > 0
            && lines[^1].Ending.Length == 0;

        if (index > 0 && lines[index - 1].Ending.Length == 0)
        {
            lines[index - 1] = new Line(lines[index - 1].Text, ending);
        }

        for (var i = 0; i < texts.Length; i++)
        {
            var lineEnding = eofWithoutNewline && i == texts.Length - 1
                ? string.Empty
                : ending;
            lines.Insert(index + i, new Line(texts[i], lineEnding));
        }
    }

    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var index = 0;

        while (index < text.Length)
        {
            var start = index;
            while (index < text.Length && text[index] != '\r' && text[index] != '\n')
            {
                index++;
            }

            var lineText = text[start..index];
            string ending;

            if (index >= text.Length)
            {
                ending = string.Empty;
            }
            else if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                ending = "\r\n";
                index += 2;
            }
            else
            {
                ending = text[index].ToString();
                index++;
            }

            lines.Add(new Line(lineText, ending));
        }

        return lines;
    }

    private readonly record struct PublishJob(string Id, int Start, int End);

    private readonly record struct Line(string Text, string Ending);

    private readonly record struct StepEnvInspection(
        bool Present,
        bool HasEnvBlock,
        int InsertIndex);
}
