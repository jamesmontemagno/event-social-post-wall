# Documentation website

The docs use VitePress with local full-text search. They need no application API,
database, or external search account.

```powershell
npm ci --prefix docs
npm run dev --prefix docs
```

Build and preview the production site:

```powershell
npm run build --prefix docs
npm run preview --prefix docs
```

The dev and preview servers bind to loopback. VitePress fails the build for broken
internal page links. CI builds docs; it does not publish them.

VitePress 1.6's default Vite 5 dependency has published development-server
advisories. The docs package explicitly overrides it with patched Vite 6.4.4 or
newer in that major, compatible with its Vue plugin. Keep the override until
upstream ships a stable release with a patched dependency; validate build,
preview, and `npm audit --prefix docs` when updating.

## Publish later to GitHub Pages

::: warning Publication is a separate decision
A private repository does not guarantee a private Pages website. Do not enable
publishing until you intend the rendered documentation to be accessible to its
configured audience. The initial repository setup does not publish this website.
:::

1. Confirm your GitHub plan supports Pages for the repository's visibility.
2. In **Settings > Pages**, choose **GitHub Actions** as the source.
3. Create the `github-pages` environment and configure reviewers if desired.
4. Set repository variable `ENABLE_DOCS_PUBLISH=true`.
5. Run **Publish documentation** on `main`, explicitly checking the public
   publication confirmation.

The workflow builds only documentation, not the public event wall. It defaults to
`/<repository-name>/` so forks work. For a custom domain or user/organization Pages
root, set repository variable `DOCS_BASE=/` and configure the domain/TLS in Pages.
For local testing of a project URL, set the same `DOCS_BASE` before building.

If you fork the project, update the repository, edit, and security-report links in
the docs configuration and community files to your own repository.
