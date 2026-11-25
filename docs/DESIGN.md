# PR Review Guard: design notes

Status: version 1 deployed on 2026-11-24, report-only, on the sandbox repository.

## 1. Problem

A reviewer approves a large pull request (PR) in less than one minute.
Example: 450 changed lines, approved in 50 seconds.
The reviewer probably did not read the code.
Today nobody sees this. The decision makers (the CTO) must get a report, and the merge must wait.

## 2. What the tool does

1. It receives an event each time a reviewer votes on a PR.
2. It compares the review time with the number of changed lines.
3. If the PR is large and the approval was fast, it checks the reviewer's comments.
4. If the comments do not show a real review, it alerts the CTO.
5. It posts a status on the PR. A branch policy blocks the merge until the status is `succeeded`.

## 3. Scope

| In scope (version 1) | Out of scope (later) |
|---|---|
| Azure DevOps Repos | GitHub |
| Detect large + fast approvals | LLM judgment of comment quality |
| Check that inline comments exist | Dashboards and trend reports |
| Alert the CTO | Per-team thresholds |
| Block the merge with a status check | Automatic removal of the approval |
| Audit log of every decision | |

## 4. Flow

```
Push to PR
  -> validation pipeline -> git diff --numstat -> PR property "lines=450"

Reviewer votes Approve (vote = 10)
  -> service hook "Pull request updated" -> Azure Function (HTTP trigger)
       -> GET PR properties  (line count)
       -> GET PR iterations  (time of the last push)
       -> GET PR threads     (reviewer comments)
       -> run the rule
       -> write the result to Table Storage (audit)
       -> POST PR status "review-guard/review-depth"
       -> if flagged: send the alert to the CTO

Branch policy "Require status check: review-guard/review-depth"
  -> PR cannot complete until the status is "succeeded"
```

The PR is the shared store. The pipeline writes the line count onto the PR.
The Function reads it at approval time. The pipeline and the Function never call each other.
The Function keeps no state between events.

## 5. Components

| # | Piece | Where | Job |
|---|---|---|---|
| 1 | Service hook, "Pull request updated" | Azure DevOps project settings | Calls the Function on each vote |
| 2 | Azure Function, HTTP trigger, Consumption plan | Azure | Receives the event, runs the rule |
| 3 | Storage account | Azure | Required by Functions. Table Storage holds the audit log. |
| 4 | Identity for Azure DevOps API calls | Azure + Azure DevOps | PAT in Key Vault (different tenants, see section 12) |
| 5 | Alert channel | Azure Monitor log alert + action group (email) | Message to the CTO |
| 6 | Application Insights | Azure | Logs and failures |
| 7 | Validation pipeline | Azure DevOps | Counts changed lines on each push |
| 8 | Branch policy: status check | Azure DevOps | Blocks the merge |

No Logic App. The Function sends the alert itself, so all logic stays in one place.

Cost: a few dollars per month at normal PR volume. The $5,000 credit is much more than needed.

## 6. The rule (version 1)

All numbers below are first proposals. Tune them in the report-only phase.

```
lines < 200                         -> succeeded (small PR, no check)
review time > allowed time          -> succeeded (not fast)
large + fast, has inline comments   -> succeeded (version 1), LLM judge (later)
large + fast, no inline comments    -> failed + alert
line count missing or stale         -> pending, check again when the pipeline completes
```

- **Review time** = time of the approval event minus the later of: last push, or the time the
  reviewer was added. Azure DevOps does not store a vote time. Use the `createdDate` of the
  service hook event.
- **Allowed time** = a number of seconds per 100 changed lines. First proposal: 30 s per 100 lines.
- **Line count** excludes generated files (lock files, migrations, designer files). The pipeline
  applies the exclude list.
- **Inline comment** = a thread by this reviewer that has a file path and a line number.
  A general comment such as "LGTM" does not count.

## 7. Azure DevOps specifics

Verify each call against the current REST API documentation before the build. These are from
the design discussion, not from a tested call.

| Need | How |
|---|---|
| Approval event | Service hook event `git.pullrequest.updated`. Read `resource.reviewers[].vote`. 10 = approved, 5 = approved with suggestions. |
| Line count | Not in the API. The pipeline runs `git diff --numstat` and writes the PR Properties API. |
| Last push time | PR iterations API: `createdDate` of the latest iteration |
| Comments | PR threads API: `threadContext.filePath` and the line position mark an inline comment |
| Block the merge | PR statuses API + branch policy "Require status check" |

Status contract (the branch policy matches on `genre` + `name`):

```json
{
  "state": "failed",
  "description": "450 lines approved in 50 s, no inline comments",
  "context": { "genre": "review-guard", "name": "review-depth" }
}
```

| State | Policy result |
|---|---|
| `succeeded` | Merge allowed |
| `failed` | Merge blocked |
| `pending` | Merge blocked, still checking |
| none | Merge blocked (when the policy is Required) |

## 8. Security

- The Function URL is public. Use the Function key in the URL and a shared secret header set in
  the service hook. Reject any call without both.
- Set the branch policy to accept the status only from the Function's identity. If not, a
  developer can post `succeeded` by hand.
- Keep the permission "Bypass policies when completing pull requests" to very few people.
  Each bypass is in the PR history.
- No secrets in config. Managed Identity first, Key Vault second.

## 9. Failure modes

| What happens | Result | Handling |
|---|---|---|
| Function is down | No status, every PR is blocked | Start with the policy as Optional. Add an alert on Function failures. |
| Approval arrives before the pipeline finishes | Line count missing or old | Post `pending`. A second service hook on "build completed" starts a new check. |
| Reviewer read the code in the IDE, then approved fast | False alarm | Clock starts at the last push or review request. Inline comments clear the flag. |
| Reviewers learn the rule and wait 2 minutes | Rule is bypassed | Comment check, and later the LLM judge |
| Vote changes (approve, reset, approve) | Duplicate events | Use the latest approval only. Make the handler idempotent (same event twice gives the same result). |
| Push after approval | Old approval on new code | Policy: reset votes on new push. Policy: reset status when the source branch changes. |
| Service hook delivers the same event twice | Duplicate alert | Store the event ID in Table Storage, skip known IDs |
| Too many alerts | People stop reading them | Report-only phase first, tune the thresholds |

## 10. Rollout

1. **Report only (2 weeks).** Policy is Optional. No block. Alerts go to one person. Count false alarms.
2. **Tune.** Change the line threshold, the allowed time and the exclude list.
3. **Enforce.** Policy is Required on the main branch.

## 11. Later versions

- **LLM judge.** Only for large + fast approvals that have comments. Send the comment and 10 to
  20 lines of diff around it, not the full diff. Ask: "Does this comment refer to this code?
  Answer yes or no, with a short reason." Store the reason for the CTO.
- **GitHub.** Same rule. Different input: `pull_request_review` event, `submitted_at`,
  `additions` + `deletions` on the PR, and a required check to block the merge.

## 12. Decisions (2026-11-24)

| # | Question | Decision |
|---|---|---|
| 1 | Same Entra tenant for Azure and Azure DevOps? | No. The `DigiCovai` org uses Microsoft accounts (tenant `0000...`). Use a PAT stored in Key Vault, read by the Function's Managed Identity. |
| 2 | Alert channel | Email through an Azure Monitor log alert on the `ALERT PR` log line (every 5 minutes). Teams free has no Workflows webhooks. The Teams sender stays in the code for a Microsoft 365 account later. |
| 4 | Vote 5 ("approved with suggestions") | Same rule as vote 10. |
| 5 | First thresholds | 200 lines, 30 s per 100 lines. Both are app settings. |
| - | Code repository | Public GitHub repository. No secrets in the repository. |
| - | Hosting | Flex Consumption, .NET 10 isolated, Central India. Storage with shared keys off, Managed Identity only. |
| - | Approval time | The first time the Function sees an approval for a reviewer + source commit is stored. Later events (build completed, vote again on the same commit) reuse it. A new push resets votes, so a new commit gets a new time. |
| - | Several approvers | One PR status. The worst result wins (failed, then pending, then succeeded). |
| - | Build hook | `build.complete` with resource version 2.0. Version 1.0 has no PR number or repository. |
| - | Sandbox | Repo `review-guard-sandbox` in project `Internal` guards branch `trunk`, not `main`: project-wide policies on `main` block direct pushes, and the setup script must push the pipeline file. |

## 13. Open questions

1. Who can clear a `failed` status? Proposal: a second reviewer's approval, or a new inline
   review by the same reviewer.

## 14. Phase 1 changes (planned, see ROADMAP.md)

1. **Mark the event after the status.** Today `TryMarkEventAsync` runs first. If the snapshot
   call or the status post fails, Azure DevOps retries with the same event id, the retry is
   skipped as a duplicate, and the PR has no status. Change: check the id first, but write it
   only after the status is posted. A repeat run is safe because the approval time is stored
   once (GetOrAdd) and the alert has its own `AlertSent` flag.
   Test: the snapshot call throws once, and the retry with the same id still posts a status.
2. **Reviewer added time.** Azure DevOps writes a system thread when a reviewer is added
   (`properties.CodeReviewThreadType` = `ReviewersUpdate`). Use its `publishedDate` as
   `ReviewerAddedAt`. Today the service passes `null`.
