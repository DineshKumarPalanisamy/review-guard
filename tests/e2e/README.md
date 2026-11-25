# End-to-end test

`Run-E2E.ps1` runs real pull requests in the sandbox repository and checks the
`review-guard/review-depth` status that the deployed Function posts.

## Before you run

1. Azure CLI with the `azure-devops` extension: `az extension add --name azure-devops`
2. Sign in to Azure DevOps yourself: `az devops login --organization https://dev.azure.com/DigiCovai`.
   The script never reads or stores the PAT.
3. The Function is deployed, the service hooks (PR updated, build completed) point to it,
   and the build validation policy on `main` of `review-guard-sandbox` runs the
   `review-guard-linecount` pipeline on each PR.

## Run

```powershell
./tests/e2e/Run-E2E.ps1
./tests/e2e/Run-E2E.ps1 -Scenarios large-fast,small
```

## Scenarios

| Scenario | What it does | Expected status |
|---|---|---|
| `large-fast` | 450 lines, wait for the pipeline, approve at once | `failed` |
| `small` | 50 lines, wait for the pipeline, approve at once | `succeeded` |
| `large-with-comment` | 450 lines, one inline comment on line 10, approve at once | `succeeded` |
| `approve-before-pipeline` | 450 lines, approve before the pipeline ends | `pending`, then `failed` |
| `critical-small` | 30 lines under `src/SampleApp/Payments/` (a critical path), approve at once | `failed` |

Each scenario abandons its PR and deletes its branch at the end, also on failure.
The exit code is 1 if any scenario fails.

## Assumptions

- The PR author can vote on their own PR (the sandbox has no "prohibit self approval" policy).
- The pipeline counts `.cs` files under `/e2e/` (they are not on the exclude list).
- The status policy is Optional (report-only), so a `failed` status does not stop the cleanup.
