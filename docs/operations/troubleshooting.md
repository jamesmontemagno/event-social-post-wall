# Troubleshooting

Start with the exact role, environment, URL, and failing step. Check Aspire logs
locally or the affected App Service's application/deployment logs in Azure.
Redact tokens, cookies, passwords, connection strings, and attendee text before
sharing logs.

| Symptom | Check |
| --- | --- |
| `vite`/`vitest` not found | Run `npm ci --prefix src\EventWall.Web` |
| .NET SDK not found/unsupported | Install the .NET 10 SDK and restart the terminal |
| Local ports in use | Stop only the identified old launcher/process; do not kill every `dotnet` process |
| Empty/paused new real event | New non-demo intake starts closed; open it in Admin |
| Wrong asset role at startup | Rebuild and deploy the matching Vite/MSBuild/runtime role from a clean output directory |
| Submission rejects `index.html` | A React package was copied to the Blazor site; deploy a clean Submission package |
| Hosted Public will not start | Set its own HTTPS `Public__SubmissionSiteUrl`; no remote default is supplied |
| Admin navigation link missing | Set/validate `Public__WallSiteUrl` and `Public__SubmissionSiteUrl`; inspect the warning |
| Event slug conflict | Use the original slug for that database or provision a separate event database |
| Missing SQL/table/migration errors | Run the setup tool with the deployment identity; keep app runtime accounts restricted |
| SQL login/network failure | Correct per-role user/password, database name, TLS, firewall, app outbound IPs |
| Database purpose mismatch | Staging uses `EventWallStaging`/Demo; Production uses `EventWallProd`/Production |
| Staff login unavailable | Admin role plus a configured `STAFF_PASSCODE` of at least 12 characters |
| Moderator suddenly unauthorized | Account disabled/rotated, owner passcode changed, or expired cookie; sign in again |
| Staff write gets 400 | Refresh/re-login for a fresh CSRF token; do not bypass CSRF |
| Question gets 429 | IP/browser budgets, shared venue NAT, or global limiter; honor `Retry-After` |
| Question token expired | Use the form's explicit refresh/retry; don't auto-loop submissions |
| Turnstile 403/503 | Widget hostname/action, matching keys, script/network access, verification service status |
| Blazor reconnects repeatedly | WebSockets, proxy timeouts, affinity, app memory, restarts, blocked browser storage |
| Approved content not immediately visible | Host refresh is intentional; check selected sort, pins, and new-post count |
| Withdrawn content remains visible | Next successful 60-second/visibility check; offline walls keep their last snapshot |
| Promo missing | Staff activation and the host's local star/show-promos toggle |
| Images missing | Source/media allowlist, HTTPS, upstream removal, or content policy filters |
| Source configured but not collecting | Demo off, Enabled/PolicyApproved true, credentials, source-specific quota/control |
| YouTube idle | Owner must explicitly Start collection; check active live chat ID |
| X budget exhausted | Shared post/profile reservation headroom, UTC-day usage, explicit spend gate |
| Twitch reauth setting ignored | Persisted rotated token takes precedence; follow the scoped reconnect procedure |
| GitHub Azure release skipped | `ENABLE_AZURE_DEPLOYMENT=true`, manual dispatch on `main` |
| OIDC login fails | Exact repo/environment subject, tenant/client/subscription values, federated credential and RBAC scope |
| Production gate fails | Required reviewers configured and supported by your GitHub plan; API permission to verify them |
| Staging readiness probe fails | Access protection, correct role URLs, runtime configuration, and actual app readiness |
| Docs publication skipped | Explicit `ENABLE_DOCS_PUBLISH`, main branch, public-publication confirmation |
| Pages CSS/links broken | Correct `DOCS_BASE` for project Pages vs custom domain |

For provider errors, follow the specific [platform guide](../platforms/index.md).
Do not fix a failed rehearsal by enabling broad SQL permissions, disabling
authentication, ignoring provider approval, or deploying admin assets publicly.

## Useful checks

```powershell
dotnet --info
node --version
Invoke-RestMethod http://127.0.0.1:5080/api/health
Invoke-RestMethod http://127.0.0.1:5080/api/public/event
```

Health only confirms the process is responding. A wall read exercises database
access; Submission's bootstrap intentionally does not. Rehearse a real protected
question/approval flow using synthetic data for end-to-end readiness.

## Getting help

Include the commit/release, OS, SDK/Node versions, role/environment, sanitized
error, and minimal reproduction. State whether it reproduces in a clean local
demo. Never attach a production database or raw access tokens.
