# Security policy

## Supported versions

Native Widget is currently alpha software. Security fixes are applied to the latest release and
the `main` branch only.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting for this repository. Do not open a public
issue containing credentials, OAuth tokens, personal notes, calendar data or exploit details.

Include the affected version, reproduction steps, expected impact and any suggested mitigation.
You should receive an acknowledgement within seven days.

## Credential storage note

The current alpha stores integration configuration under `%AppData%\NativeWidget`. Treat the
Windows account as the security boundary and avoid using production credentials on a shared or
untrusted machine. Credential hardening is tracked as pre-1.0 work.
