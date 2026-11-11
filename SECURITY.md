# Security

## Reporting

Do not post credentials, attendee information, or exploitable vulnerabilities in
public issues. Use
[GitHub private vulnerability reporting](https://github.com/jamesmontemagno/event-social-post-wall/security/advisories/new).
If reporting is unavailable while the repository is private, contact the repository
owner through your existing private GitHub collaboration channel. Do not publish
details just to obtain a response.

Include affected versions, reproduction steps using synthetic data, impact, and
any proposed mitigation. There is no guaranteed response time or commercial SLA.
The current default branch is the supported development line; no LTS branches
are promised.

## Deployment responsibilities

- Use HTTPS, distinct role hostnames, strong staff credentials, and least-privilege
  runtime SQL users. Provider credentials belong only on Admin.
- Keep secrets in protected runtime settings or a secret manager, not Git, Vite
  variables, workflow artifacts, logs, or issue attachments.
- Persist and protect separate Admin and Submission Data Protection key rings.
- Restrict staging access. `robots.txt` and `noindex` are not authentication.
- Test abuse controls, backups, restores, and provider deletion limitations.
- Use one admin collector instance and one Submission instance until distributed
  coordination and rate enforcement have been implemented.

See [security and privacy](docs/operations/security.md) for operational details.
An MIT license and a passing test suite are not a security certification.
