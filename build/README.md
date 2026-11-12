# build/ — Nuke build project and the NuGet Trusted Publishing stopgap

## Why this patch exists

NukeBuildHelpers 9.0.51 can emit workflow- and job-level environment
variables, but it cannot emit:

- a job-level `permissions:` block,
- a job-level `if:` gate that keeps the publish jobs off pull requests,
- a `NuGet/login` (OIDC) step before the generated `NUKE_RUN` step,
- a step-scoped token environment variable that overrides the workflow-level one.

NuGet Trusted Publishing needs a short-lived token minted through GitHub OIDC:
each publish job must grant `id-token: write`, the login step must run before
the Nuke publish step, and `NUKE_NUGET_AUTH_TOKEN` must carry the minted token
for that step only. Without the patch the publish jobs have no OIDC permission,
no job gate, and no token-minting step. There is no long-lived-secret fallback:
on the bump path `build/Build.cs` throws `InvalidOperationException` when
`NUKE_NUGET_AUTH_TOKEN` is empty.

The durable fix belongs upstream in NukeBuildHelpers. Until then,
`build/WorkflowPatcher.cs` patches the generated workflow and `build/Build.cs`
wires it in through the `PatchGithubWorkflow` target
(`.TriggeredBy(GithubWorkflow).Unlisted()`).

## What the patch does

The patcher discovers every top-level job whose ID ends in `PUBLISH` and
requires the discovered set to equal the expected set exactly (order does not
matter). The expected set is `WorkflowPatcher.ExpectedPublishJobs`:

| Publish job ID |
| --- |
| `NET_CONDUIT_PUBLISH` |
| `NET_CONDUIT_TRANSIT_DELTA_MESSAGE_PUBLISH` |
| `NET_CONDUIT_TRANSIT_DUPLEX_STREAM_PUBLISH` |
| `NET_CONDUIT_TRANSIT_MESSAGE_PUBLISH` |
| `NET_CONDUIT_TRANSIT_STREAM_PUBLISH` |
| `NET_CONDUIT_TRANSPORT_IPC_PUBLISH` |
| `NET_CONDUIT_TRANSPORT_QUIC_PUBLISH` |
| `NET_CONDUIT_TRANSPORT_TCP_PUBLISH` |
| `NET_CONDUIT_TRANSPORT_UDP_PUBLISH` |
| `NET_CONDUIT_TRANSPORT_WEBSOCKET_PUBLISH` |

### Keeping the expected set in sync

`WorkflowPatcher.ExpectedPublishJobs` in `build/WorkflowPatcher.cs` must stay in
sync with `DeploymentApps` in `build/Build.cs`. NukeBuildHelpers derives each
publish job ID as `AppId.ToUpperInvariant() + "_PUBLISH"` (`net_conduit`
becomes `NET_CONDUIT_PUBLISH`). Adding or removing a package requires updating
four places in the same change:

1. `DeploymentApps` in `build/Build.cs`
2. `WorkflowPatcher.ExpectedPublishJobs` in `build/WorkflowPatcher.cs`
3. `ExpectedPublishJobIds` in
   `build/WorkflowPatcher.Tests/WorkflowPatcherTests.cs`
4. the publish-job table above

The patcher fails closed on a missing or unexpected publish job, so a mismatch
fails `PatchGithubWorkflow` and the `./build.sh GithubWorkflow` run instead of
silently granting OIDC permissions. A missing, renamed-out-of-suffix, or
unexpected `*PUBLISH` job throws with diagnostics naming the missing and
unexpected IDs. A duplicate top-level job ID also throws. The legacy top-level
`NUKE_NUGET_AUTH_TOKEN` line is removed once, before job discovery.

For every publish job:

1. Inserts the job-level permissions block immediately before `steps:`:

   ```yaml
   permissions:
     contents: read
     packages: write
     id-token: write
   ```

2. Rewrites the publish job's single-line `if:` so its first top-level
   conjunct is `github.event_name == 'push'`:

   ```yaml
   if: "github.event_name == 'push' && (! failure() && ...)"
   ```

   The job-level gate is the security boundary. On a pull request (including
   forks and Dependabot) or any future non-push trigger
   (`pull_request_target`, `workflow_dispatch`, ...) the job is skipped, so
   `id-token: write` and `packages: write` never reach PR-controlled code.
   `push` is used instead of a negative `!= 'pull_request'` gate because the
   workflow currently triggers only on `push` and `pull_request`: the positive
   form covers branch pushes and `bump-*` tag pushes and fails closed for
   events added later.

   The gate is fail-closed. The patcher throws `InvalidOperationException`
   naming `if:` and the publish job when the job-level `if:` is missing, empty,
   a quoted-empty scalar (`if: ''` / `if: ""`), a block scalar (`|`/`>` and
   variants), unterminated, followed by a trailing comment, duplicated, or a
   plain multi-line continuation. It also throws when the condition starts
   with the push gate but is not exactly that gate applied to one balanced
   parenthesized expression (for example a trailing `|| true`,
   `|| github.event_name == 'pull_request'`, an extra gate term outside the
   parentheses, a missing `}}`, or unbalanced parentheses), and when a
   `github.event_name` term appears anywhere else (for example
   `github.event_name != 'pull_request' || true` or a gate appearing later).
   The rewrite is re-validated before emission with the same exact-shape
   check: an input whose emitted value would not be exactly that gate (for
   example `! failure()) || (true`) throws instead of being emitted. It never
   leaves a publish job ungated.

3. Inserts a SHA-pinned `NuGet/login` step immediately before `- id: NUKE_RUN`:

   ```yaml
   - id: nuget_login
     name: NuGet login (OIDC)
     uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
     with:
       user: Kiryuumaru
     if: github.event_name == 'push'
   ```

   The step-level `if:` is **defense-in-depth only**; the job-level gate above
   is the security boundary. On a skipped login the output is empty, and the
   Nuke step tolerates that because the token is only required on the bump
   path.

4. Adds step-level env to `NUKE_RUN`:

   ```yaml
   env:
     NUKE_NUGET_AUTH_TOKEN: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}
   ```

5. Removes the generated top-level
   `NUKE_NUGET_AUTH_TOKEN: ${{ secrets.NUGET_AUTH_TOKEN }}` line, then verifies
   that no `NUKE_NUGET_AUTH_TOKEN` referencing `secrets.NUGET_AUTH_TOKEN`
   remains anywhere in the workflow (top-level, job-level, or step-level). Any
   remaining reference fails verification. `GITHUB_TOKEN` / `NUKE_GITHUB_TOKEN`
   are untouched.

The patcher is idempotent, preserves line endings and an optional UTF-8 BOM,
handles a file whose last line has no trailing newline, and refuses to proceed
on a block-scalar, commented, or plain multi-line `run:` value. It also throws
on a missing `jobs:`/publish job/`steps:`/`NUKE_RUN`/`run:` anchor, a partial
or conflicting `permissions:` block, a duplicate or mismatching `nuget_login`
step, or a publish-job set that does not match the expected set.

## Required nuget.org Trusted Publishing policy

Configure the policy on nuget.org with exactly these values:

| Field | Value |
| --- | --- |
| User (nuget.org profile) | `Kiryuumaru` |
| Repository | `NetConduit` |
| Workflow file | `nuke-cicd.yml` |
| Environment | *(blank)* |

One policy covers all ten packages: the policy matches the repository and
workflow file, not an individual package ID. Create the policy under the
`Kiryuumaru` account; it covers packages owned by that account. The repository
is `Kiryuumaru/NetConduit`; if the policy form asks for a repository owner
separately, use `Kiryuumaru`. If a package is transferred to another owner,
create a policy under the new owner. If a protected environment is added later,
the policy's Environment field must match the job's `environment:` value.

## Secrets

The generated workflow no longer references `secrets.NUGET_AUTH_TOKEN`.
Verify and delete the repository secret now; it is unused:

```bash
grep -R "secrets.NUGET_AUTH_TOKEN" .github/workflows
# expected: no matches
```

The bump path reads the short-lived `NUKE_NUGET_AUTH_TOKEN` minted by the
`nuget_login` step and throws if it is empty. It never falls back to a
long-lived repository secret.

Deleting the repository secret only removes it from GitHub. Revoke the old
long-lived nuget.org API key itself as well (nuget.org → API keys → revoke the
key previously stored as `NUGET_AUTH_TOKEN`) so it cannot be used even if it
was copied elsewhere.

## Regeneration

Always regenerate through the Nuke target, never by hand-editing
`.github/workflows/nuke-cicd.yml`:

```bash
./build.sh GithubWorkflow
```

Do **not** use the interactive menu. Its `GithubWorkflow` entry calls
`PipelineHelpers.BuildWorkflow` directly and bypasses the Nuke target graph, so
`PatchGithubWorkflow` never runs and the workflow is left unpatched.

If someone does regenerate through the interactive menu, recover by running
`./build.sh PatchGithubWorkflow` (or re-running `./build.sh GithubWorkflow`)
and commit the result. Do not hand-edit the workflow. If the file still carries
an older gate form, re-run `./build.sh GithubWorkflow` to regenerate it fresh;
the patcher is fail-closed on conflicting `github.event_name` conditions.

## Tests

```bash
dotnet test build/WorkflowPatcher.Tests/WorkflowPatcher.Tests.csproj -c Release
```

The `net_conduit` matrix entry of the `TestEntry` target also runs this project
in CI (Release), alongside the existing test suite. The committed-workflow
guard runs from this entry (`build/Build.cs:114`): renaming or removing
`net_conduit` from `DeploymentApps` silently stops the guard from running. Keep
the invocation, or move it to an app-independent target.

## Security follow-up

The publish jobs currently have no GitHub `environment:`. The patcher neither
inserts nor verifies `environment:`; a hand-edited `environment:` line is
overwritten by the next `./build.sh GithubWorkflow`. A protected environment
(required reviewers plus a deployment policy) would stop arbitrary refs from
minting an OIDC token for nuget.org. Adopting one requires extending the
patcher to insert and verify `environment: nuget-publish` (or similar) on every
publish job, and setting the same environment name in the nuget.org Trusted
Publishing policy, in the same change. Then:

1. configure the environment's deployment policy for the refs the publish jobs
   must run on:
   - a **tag rule** for `bump-*` — the bump release path publishes from a tag;
     this rule is required.
   - branch rules for `prerelease` and `master` only if the jobs should run
     (and mint an OIDC token) on those branch pushes. The jobs run on those
     branches, but the publish path itself is gated by `RunType.Bump`
     (`build/Build.cs:239`), so branch pushes do not publish. A policy limited
     to `master` alone blocks the `bump-*` tag release path.
2. keep `id-token: write` scoped to the publish jobs only.

## Removal checklist

Remove this stopgap in this order once NukeBuildHelpers can emit the job-level
permissions and the pre-step login natively:

- [ ] upgrade NukeBuildHelpers to a version that emits the job-level
      `permissions:` block, the job-level PR gate, the `NuGet/login` step, and
      the step-scoped token env natively
- [ ] disable the `PatchGithubWorkflow` trigger (or delete the target) **before**
      regenerating, so the old patcher cannot run against a workflow that
      already has native support
- [ ] regenerate with `./build.sh GithubWorkflow` and verify every publish job
      has the native permissions, login step, job-level gate, and `NUKE_RUN`
      env
- [ ] delete `build/WorkflowPatcher.cs`, `build/WorkflowPatcher.Tests/`, and the
      `PatchGithubWorkflow` target if not already removed
- [ ] remove `using NetConduit.Build;` from `build/Build.cs` (otherwise the
      build fails with CS0246), the `<Compile Remove="WorkflowPatcher.Tests/**/*.cs" />`
      item from `build/_build.csproj`, the `build/WorkflowPatcher.Tests/...`
      entry from `NetConduit.slnx`, and the `DotNetTest` block for
      `build/WorkflowPatcher.Tests` at `build/Build.cs:114-121`
- [ ] regenerate `.nuke/build.schema.json` and
      `.github/workflows/nuke-cicd.yml` with `./build.sh GithubWorkflow`
- [ ] verify again that every publish job is gated and has the expected
      permissions/login/env
- [ ] confirm the `NUGET_AUTH_TOKEN` repository secret is deleted and the old
      nuget.org API key is revoked (it is unused as of this patch; see the
      Secrets section)
- [ ] update or delete this README
