# Review Guard: roadmap

## Objective

Review Guard tells a decision maker which approved PRs show no evidence of a real review. It
sends no code outside the company, and it proves its accuracy with numbers.

Version 1 does a narrower job: it catches large PRs approved soon after a push (450 lines must
be approved in under about 2 minutes to fail). The changes below close that gap.

## What a reader must conclude, and what proves it

| Conclusion | Proof in the repository |
|---|---|
| The limit of the first design was found and fixed | DESIGN.md: why time alone fails, why evidence replaces it |
| Security came first | Read-only access, no code leaves the tenant |
| Accuracy is measured, not guessed | Precision and recall of the old rule and the new rule on labelled PRs |
| It can run on Monday | Works with zero config, one deploy script, a 2-minute demo |

## Phase 1: correctness and trust

| # | Change | Where | Why |
|---|---|---|---|
| 1 | Mark the event as seen only after the status is posted | `ReviewGuardService`, `TableAuditStore` | Today the event is marked first. If a later call fails, the retry looks like a duplicate and the PR gets no status. |
| 2 | Read the "reviewer added" time from the system threads | `AzureDevOpsClient` | `ReviewerAddedAt` is always `null` today, so late-added reviewers get a too-long clock |

## Phase 2: the evidence rule

| # | Change | Where | Why |
|---|---|---|---|
| 3 | Get the changed file paths from the iteration changes API (`$compareTo=0`, with paging) | `AzureDevOpsClient`, `PullRequestSnapshot` | Critical paths (#5) need to know which files changed, from a source the author can't edit |
| 4 | Replace the time gate with a list of evidence signals. Time becomes one signal. | `ReviewDepthRule`, `ReviewInput`, `ReviewDecision` | The clock starts at the last push. A reviewer who waits a few minutes and then approves without reading always passes. |
| 5 | Default critical paths plus a pure `PathMatcher` | `ReviewDepthOptions`, new file in Core | "Payment code changed, nobody commented" matters more than "big PR". Test with the sample files in `review-guard-sandbox` (`src/SampleApp/Auth`, `src/SampleApp/Payments`). |
| 6 | Count unresolved Copilot review threads at approval time | `AzureDevOpsClient`, rule | A strong signal that needs no code access |
| 7 | The alert lists the reasons, not one sentence | `AlertMessage`, `TeamsAlertSender` | The decision maker sees why without opening the PR |

## Phase 3: proof

| # | Change | Why |
|---|---|---|
| 8 | Label 30 to 50 PRs by hand, replay them through the old and new rule, publish precision and recall | The rule is a pure function, so a replay is cheap. Needs real PRs, not the sandbox e2e PRs. |
| 9 | GitHub Actions workflow that runs `dotnet test` on every push | No CI for the tool's own code today |
| 10 | Rewrite the README: problem, solution, diagram, numbers, how to run | The current README is an operations manual |
| 11 | 2-minute demo video | A reader watches before reading |

## Also needed before enforcement

- Alert when the Function fails (with a Required policy, a broken Function blocks every PR).
- Alert or reminder before the PAT expires (25/12/2026).
- Decide who can clear a `failed` status (DESIGN.md section 13).

## Known gap, accepted for now

The line count script runs from the PR's own branch, so a PR author can change it and fake the
count. Not fixed in this roadmap.

## Not now

- GitHub adapter
- AI summary of the diff (optional, self-hosted, only after Phase 3)
- Git-history hotspots, dashboards, per-team thresholds

Finish the proof first. A measured rule beats an unmeasured model.
