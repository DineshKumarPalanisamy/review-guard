# Review Guard

Flags large pull requests that were approved too fast in Azure DevOps, posts a PR status, and
alerts the CTO. A branch policy can block the merge on that status.

Design and decisions: [docs/DESIGN.md](docs/DESIGN.md).

## How it works

```
push to PR  -> line count pipeline -> PR properties ReviewGuard.ChangedLines / SourceCommit
vote        -> service hook        -> Function /api/hooks/pull-request-updated -> rule -> PR status
build done  -> service hook        -> Function /api/hooks/build-completed      -> rule -> PR status
```

PR status: genre `review-guard`, name `review-depth`. States: `succeeded`, `failed`, `pending`.

## Repository layout

| Path | What |
|---|---|
| `src/ReviewGuard.Core` | The rule and the service. No I/O. |
| `src/ReviewGuard.Functions` | Azure Function (.NET 10 isolated): HTTP triggers, Azure DevOps client, Table audit, Teams alert |
| `src/ReviewGuard.Functions/critical-paths.json` | Critical paths per project and repository. Ships with the Function. |
| `tests/ReviewGuard.Core.Tests` | Unit tests (`dotnet test`) |
| `tests/e2e` | End-to-end test against the sandbox repo |
| `pipelines` | Line count pipeline (copy into each guarded repo) |
| `infra/main.bicep` | Flex Consumption Function, Storage (no shared keys), Key Vault, App Insights |
| `scripts/deploy-azure.ps1` | Deploy infra and code |
| `scripts/setup-ado.ps1` | Repo, pipeline, policies, service hooks in Azure DevOps |

## Deploy

1. Azure: `az login`, then `./scripts/deploy-azure.ps1`.
2. Store an Azure DevOps PAT (scopes: Code Read, Code Status) in Key Vault as secret `AdoPat`.
3. Azure DevOps: set `AZURE_DEVOPS_EXT_PAT` (or `az devops login`), then run
   `./scripts/setup-ado.ps1 -FunctionBaseUrl <url> -FunctionKey <key> -WebhookSecret <secret> -TargetBranch <branch>`.
   Without `-PolicyBlocking` the status check is optional (report-only phase).
4. Alert email: `./scripts/deploy-azure.ps1 -AlertEmail <address>` adds an Azure Monitor log alert.
   It finds the `ALERT PR ...` log lines every 5 minutes and emails each one.
   Optional: a Teams Workflows webhook URL as app setting `ReviewGuard__TeamsWebhookUrl`
   (needs a Microsoft 365 work account; Teams free cannot receive webhooks).

## Settings

| App setting | Default | Meaning |
|---|---|---|
| `ReviewGuard__MinLines` | 200 | Smaller PRs are not checked |
| `ReviewGuard__SecondsPer100Lines` | 30 | Allowed review time |
| `ReviewGuard__MinRiskyReviewSeconds` | 60 | Lowest allowed review time when the PR touches critical paths or has open bot threads |
| `ReviewGuard__BotReviewerNames__0` | empty | Display name of a bot reviewer (for example Copilot). Its open threads make a PR risky. Empty = off |
| `ReviewGuard__OrganizationUrl` | | `https://dev.azure.com/<org>` |
| `ReviewGuard__Pat` | Key Vault ref | Azure DevOps PAT |
| `ReviewGuard__WebhookSecret` | Key Vault ref | Must match the `X-ReviewGuard-Secret` header |
| `ReviewGuard__TeamsWebhookUrl` | empty | Alert channel |

## Test

```
dotnet test
./tests/e2e/Run-E2E.ps1 -TargetBranch trunk
```
