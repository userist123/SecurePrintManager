# SecurePrintManager
Windows-first secure print control platform.

Layers:
- Core: domain, authorization, quota, encryption, audit, print state machine.
- Database: EF Core persistence.
- Service: privileged Windows Service boundary.
- UI: WPF client; never trusted for authorization.
- Tests: deterministic unit tests.

Security principle: the UI supplies requests; the Service is the enforcement boundary.
