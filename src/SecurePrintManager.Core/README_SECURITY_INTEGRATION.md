# Security integration checkpoint

This document records the intended security boundary while the application is being hardened.

## Required before production

- Provision `SECUREPRINT_FILE_KEY` through a protected Windows secret mechanism, not source control.
- Add database fields/migrations for hashed PIN and card identifiers before enabling the new authentication methods.
- Keep all authorization/enforcement decisions in the Windows Service.
- Expose only validated, authorized operations over named-pipe IPC.
