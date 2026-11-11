# Event Social Post Wall frontend

One React/TypeScript/Vite project builds two isolated sites:

- `VITE_SITE_ROLE=public`: the host wall (default).
- `VITE_SITE_ROLE=admin`: the private staff console.

The attendee question site is Blazor Server in the API project, not a Vite role.
Start the full local stack using the [root quickstart](../../README.md).

```powershell
npm ci
npm test
npm run lint
npm run build
```

Never put secrets in `VITE_*` variables, mix admin assets into a public package,
or expose Vite's development server to the internet.
See [architecture](../../docs/architecture.md),
[configuration](../../docs/configuration.md), and
[contributing](../../CONTRIBUTING.md).
