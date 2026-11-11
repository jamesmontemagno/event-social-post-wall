# Cloudflare Turnstile

Turnstile can add a human-verification challenge before questions reach SQL.
It is optional locally and does not replace moderation or all abuse controls.
Cloudflare DNS/proxy service is not required.

## Create a widget

1. Open [Cloudflare Turnstile](https://dash.cloudflare.com/?to=/:account/turnstile)
   and add a widget.
2. Choose Managed mode and register every question hostname, including a direct
   App Service hostname if you intend to use it.
3. Copy the public **Site Key** and private **Secret Key**.
4. Configure **Submission only**:

   ```text
   Turnstile__SiteKey=<public-site-key>
   Turnstile__SecretKey=<private-secret>
   Turnstile__Required=true
   Turnstile__Hostnames__0=questions.example.com
   Turnstile__Hostnames__1=your-prefix-submit.azurewebsites.net
   ```

Set hostname values to hostnames only, not URLs. Use separate staging/production
widgets when that suits your boundary; otherwise allow both environments in the
widget and verify the host allowlist.

The Bicep template supports the site key and secure secret parameters. Hostname
allowlisting can be set on each Submission app afterward.

## Behavior

- Both keys absent: disabled unless `Required=true`.
- One key absent: startup fails.
- Invalid/missing challenge: HTTP 403 before SQL.
- Verification outage, rejected secret, or malformed response: HTTP 503; never
  treated as a successful challenge.
- Tokens are single-use; the widget resets after every send, including retries.

The server validates action `submit-question` and configured hostnames after the
existing token/rate/input guards. The browser loads
`https://challenges.cloudflare.com/turnstile/v0/api.js`; networks that block it
cannot submit while the challenge is required.

Cloudflare processes challenge signals under its terms. Include that disclosure
in your event's privacy notice.

## Local test

Use only [Cloudflare's official dummy test keys](https://developers.cloudflare.com/turnstile/troubleshooting/testing/)
for automated/local tests, never production. Dummy responses report `example.com`,
so do not configure production hostname restrictions for that test configuration.
The app's automated tests use fake verification responses rather than real tokens.

Official references: [getting started](https://developers.cloudflare.com/turnstile/get-started/),
[server validation](https://developers.cloudflare.com/turnstile/get-started/server-side-validation/).
