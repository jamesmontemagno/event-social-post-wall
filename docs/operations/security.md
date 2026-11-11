# Security and privacy

## Secrets and trust boundaries

- Use distinct role origins and role-specific build artifacts.
- Keep provider credentials and the owner passcode on Admin only.
- Keep Turnstile's secret on Submission only; its site key is intentionally public.
- Use separate staging/production credentials and restricted SQL users per role.
- Store secrets in protected App Service configuration or approved secret-store
  references. Do not commit `.env`, connection strings, publish profiles, database
  files, key rings, or copied logs containing secrets.
- Treat user-secrets as a local convenience, not encrypted credential storage.
- Do not put credentials in any `VITE_*` setting; those values ship to browsers.

Data Protection keys persist outside deployment packages and are distinct by role.
File-system persistence by itself does not encrypt a key ring at rest; use private
filesystem permissions and a reviewed at-rest protection/secret-store design for
your threat model.

## Staff access

Admin login is internet-reachable in the baseline, but privileged operations need
authenticated staff cookies and CSRF protection. Cookies are host-only, HttpOnly,
SameSite Strict, and secure on hosted environments. Login attempts are rate limited.
Named member sessions validate the account's enabled/version state on each request.
Only hashes of moderator access codes are stored.

Use strong unique owner credentials, invite named moderators, and revoke access
after the event. An allowlist, access gateway, or private network can further
restrict staff access without exposing staff endpoints on the public apps.

## Anonymous questions

The browser first receives a protected HttpOnly cookie and a signed 30-minute
submission token. Sends require both, origin validation, and a UUID idempotency
key. Text/body/name size guards and honeypot checks run before database writes.

Rate budgets apply to attempts, including invalid sends and retries:

- one attempt per three-minute fixed window per trusted client IP;
- eight attempts per sliding hour per anonymous browser;
- shared app request budget of 5,000/minute per instance.

The form never automatically loops a failed submission. Expired tokens require an
explicit refresh/retry, retaining the idempotency key for an unchanged question.
Matching retries return the original receipt; changed payloads conflict.

Tokens are not proof of a human. [Turnstile](../platforms/turnstile.md) can add
verification but cannot eliminate distributed abuse. Honeypot responses intentionally
acknowledge and discard trapped spam without SQL. Human moderation remains final.
Monitor NAT-related 429s and rehearse venue traffic.

## Browser storage and external requests

The question page stores a draft and attempted idempotency key in tab-local
`sessionStorage` to recover after disconnect/reload. It does not store submission
tokens there or automatically send a recovered draft. Successful receipt clears it.
Blocked storage produces a visible warning.

The wall stores presentation preferences locally. Remote avatars/images can cause
requests to provider/media hosts; no-referrer behavior reduces URL leakage but does
not hide attendees' network addresses from those hosts. Font files, question
avatars, and QR generation are local. Turnstile adds Cloudflare requests when enabled.

## Privacy duties

Publish a privacy notice explaining collection, moderation, public display, provider
media, challenge processing, retention, exports, and who to contact for removal.
Optional display names and public social profiles can still be personal data.
Do not assume anonymized questions or public posts are exempt from legal obligations.

Review platform terms and separate analytics/export gates. No automatic purge
service is included. Restrict exported reports and backups; don't upload real
attendee data to public issues or demo recordings.

## Staging and monitoring

Staging must be protected separately with your access gateway/authentication or
network restrictions. `robots.txt` and `noindex` are not password protection.
The release probes must be adapted if that protection blocks anonymous access.

Monitor authentication errors, 429/5xx rates, database failures, and provider
health without logging request bodies, tokens, cookies, or credentials.
An HTTP health response is not a full security or readiness assessment.

Report vulnerabilities through the repository's private reporting process, not
public issues. A clean secret scan only covers known patterns in the scanned
tree; it does not prove that no secret or vulnerability can exist.
