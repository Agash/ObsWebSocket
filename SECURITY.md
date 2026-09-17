# Security Policy

## Supported versions

Only the latest published version of ObsWebSocket receives security fixes, and while this project is
pre-1.0 that version is a prerelease. Concretely: the newest package on NuGet is what gets fixed,
whether or not it is marked stable, fixes land on the current minor, and nothing is backported to an
earlier one. If you are pinned to an older version, the fix for a report will be to move forward.

That will change at 1.0, when a stable line exists to support.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub Security Advisories:

1. Go to the [Security tab](../../security/advisories/new) of this repository.
2. Choose **Report a vulnerability**.
3. Describe the issue, the affected version, and how to reproduce it.

You should get an acknowledgement within a few days. Once the issue is confirmed, a fix will be
prepared privately and released together with an advisory crediting you, unless you would rather
stay anonymous.

## Scope

This library talks to third-party services on your behalf. Reports about credential handling, token
storage, request signing, and webhook signature verification are especially welcome. Vulnerabilities
in the upstream service itself should go to that service's own security contact.
