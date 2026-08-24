# Security Notice

Do not commit access tokens, API keys, passwords, private certificates, or other credentials to this repository.

If a credential is exposed in chat, logs, commits, screenshots, or files, revoke/rotate it immediately.

SecurePrintManager is a Windows-first privileged service. Security-sensitive changes must keep authorization and enforcement inside the Service boundary; the WPF UI is never trusted for authorization decisions.
