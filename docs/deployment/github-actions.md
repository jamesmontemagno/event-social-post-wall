# GitHub Actions and OIDC

## Workflows

| Workflow | Trigger | Purpose |
| --- | --- | --- |
| **CI** | Pull request, push to `main`, manual | Affected builds/tests/lint, role isolation, docs links, Bicep compilation |
| **Release to Azure** | Manual on `main`, explicit enablement | Build once, deploy staging, optionally promote the same artifacts |
| **Publish documentation** | Manual on `main`, explicit enablement/confirmation | Publish only the VitePress website |

No workflow deploys on a normal push. Pull requests use read-only permissions and
do not receive Azure credentials. Do not replace these triggers with
`pull_request_target` running untrusted pull-request code.

## 1. Create Azure identities

Use [GitHub-to-Azure OIDC federation](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect).
No publish profile or long-lived client secret is needed.

In Microsoft Entra ID, register an application (or use an approved user-assigned
managed identity). Add a federated credential for each environment:

```text
Issuer:   https://token.actions.githubusercontent.com
Audience: api://AzureADTokenExchange
Subject:  repo:YOUR-OWNER/YOUR-REPO:environment:staging
```

Use `...:environment:production` for production. The case and repository name must
match exactly. Separate identities per environment are recommended.

Grant the staging identity **Website Contributor** on the three staging apps and
the production identity the same role on the three production apps. Add narrowly
scoped Reader access if required by your organization's deployment tooling.
Do not grant subscription Owner or SQL server-administrator access to the release
workflow. Infrastructure creation and SQL migrations remain separate privileged steps.

## 2. Configure GitHub environments

Create `staging` and `production` under **Settings > Environments**. Restrict
deployment branches to `main`. Configure **required reviewers** on production,
and prevent self-review if your plan supports it. Require a human decision.

The production job also checks that a required-reviewer rule exists and fails
closed if it cannot verify the rule. Naming an unprotected environment
`production` is not sufficient. If your private-repository plan cannot support
required reviewers, do not disable the guard: use staging only until an approved
protected production workflow is available.

Add these **environment variables** on each environment:

| Variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | That environment's OIDC application/client ID |
| `AZURE_TENANT_ID` | Microsoft Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Target subscription |
| `AZURE_RESOURCE_GROUP` | Resource group containing the target apps |

On `staging`:

```text
AZURE_WEBAPP_STAGING               = <prefix>-stage
AZURE_WEBAPP_ADMIN_STAGING         = <prefix>-admin-stage
AZURE_WEBAPP_SUBMISSION_STAGING    = <prefix>-submit-stage
```

On `production`:

```text
AZURE_WEBAPP_PRODUCTION            = <prefix>-app
AZURE_WEBAPP_ADMIN_PRODUCTION      = <prefix>-admin
AZURE_WEBAPP_SUBMISSION_PRODUCTION = <prefix>-submit
```

These are resource names/identifiers, not passwords. Store staff/provider/SQL
secrets in protected **App Service** settings or Key Vault references, not these
workflow variables. Set repository variable `ENABLE_AZURE_DEPLOYMENT=true` only
after infrastructure, databases, identities, and gates are ready.

## 3. Release

1. Merge reviewed changes to `main`; confirm CI passes.
2. Back up the target database and apply any new migrations/runtime grants with
   the deployment identity. Review compatibility before changing a live schema.
3. Run **Release to Azure** without production promotion.
4. Rehearse all three staging roles, including real provider behavior if approved.
5. Run with `deployProduction=true` when ready. The workflow tests/builds, deploys
   staging, then waits for production approval. The production deployment uses
   the **same run's** three artifacts, not rebuilt packages.

Releases are serialized to prevent overlapping deployments. Production defaults
off. Artifacts retain for 30 days; download a known-good release for rollback
before retention expires. ZIPs contain application binaries/assets only, never
runtime connection strings or key rings.

The workflow refreshes OIDC authentication before each role deployment so a long
zip deployment does not leave subsequent role operations with an expired session.

## Maintainer release checklist

- Enable branch protections/required CI and review contributions before merging.
- Review dependency updates and generated third-party notices.
- Enable secret scanning/push protection and private vulnerability reporting
  when supported by the repository's plan/visibility.
- Confirm production reviewers exist and that environment values target the right apps.
- Keep docs publication disabled until separately approved.
- When forking, recreate environments, variables, federated subjects, and Azure role
  assignments; none are transferred by copying Git files.
