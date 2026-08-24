# Security migration note

The authentication/encryption layer now expects `PinHash` and `CardCodeHash` fields and requires `SECUREPRINT_FILE_KEY` to be provisioned outside source control.

Before merging the application to a production build, update the database model/migration accordingly and provision the secret with Windows-protected service configuration/DPAPI or an enterprise secret store.
