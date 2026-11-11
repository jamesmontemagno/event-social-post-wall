# Demo and branding

## Generic defaults

The product is **Event Social Post Wall**. Its sample event is **Community Event**
with `#communityevent`. Set your own [event settings](configuration.md) on all roles.
The visible question link comes from `Public__SubmissionSiteUrl`, not a baked-in
domain. Event names are treated as literal text, not markup or regular expressions.

The host can choose **Classic**, **Aurora**, or **Aurora pink**, and independently
choose light/dark mode. Preferences are per browser. The generic rocket decoration
respects reduced motion; expanded cards and immersive controls remain keyboard
accessible. The Space Grotesk font is self-hosted.

For deeper customization, update the shared CSS variables in the React project's
`src/index.css` and `src/skin.css`. The Blazor question package copies the shared
theme and its assets. Rebuild all affected packages; do not edit deployed output.

Question avatars are stable per question ID and use locally bundled CC0 robot
portraits. They do not identify a person or contact a third-party avatar service.
Preserve the attribution/provenance in third-party notices when redistributing them.

## Standalone demo

For production-shaped local bundles instead of Vite:

```powershell
npm ci --prefix src\EventWall.Web
.\tools\demo\start-demo.ps1
```

This starts the wall on 5080, admin on 5081, and questions on 5082, all loopback-only.
The launcher generates a random staff passcode and prints it only to your local
terminal. Sign in as `admin`. Do not share recordings containing the passcode.

- `-Port 5280`: choose another three-port range.
- `-IntervalSeconds 10`: adjust fictional post arrival.
- `-SkipWebBuild`: reuse existing role-specific demo assets.
- `-StaffPasscode`: optional local override; avoid putting real credentials in shell history.
- `-Reset`: delete only the local demo SQLite database and reseed fictional data.
  Stop every process using that database first.

Never run this launcher alongside Aspire. Demo and non-demo local databases are
separate. Hosted demo resetting is a different, explicitly confirmed operation;
see [database operations](operations/database.md).

## Optional demo recording

`tools/demo` contains Playwright smoke tests and a narrated demo recorder.
Install its dependencies with `npm ci --prefix tools/demo`. The scripts expect
Microsoft Edge (`channel: msedge`); recording also requires `ffmpeg` on PATH.
They are optional tooling, not production dependencies.

With the standalone demo running, set `STAFF_PASSCODE` in the recorder's process
to that demo's generated value and run `npm run record --prefix tools/demo`.
Outputs go under ignored `demo-output`. Only record fictional data.
