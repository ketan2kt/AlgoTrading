# Azure deployment and recovery

## Promotion status

Current architecture, reconciled 15 November 2026: paper strategies with separately gated
controlled-live execution. Live orders require the existing build, daily arming, per-market,
reconciliation and risk controls. This document does not authorize live activation.
The old Phase 7 paper-only description and “no order gateway” limitation are superseded.

## Architecture

- Azure App Service Linux Basic B1, one instance
- Azure Database for PostgreSQL Flexible Server, Burstable `Standard_B1ms`, PostgreSQL 16, 32 GiB storage, seven-day backup retention
- GitHub Actions ZIP deployment; no container registry is required
- Log Analytics with 30-day retention
- VNet-integrated App Service
- PostgreSQL on a delegated subnet with public access disabled and private DNS
- Angular production files served by ASP.NET Core from the same container and origin
- Separate App Service plan for the Groww replay recorder; Upstox backup runs on the website host.
- Private replay/key-ring blobs with managed identity, versioning and 14-day blob/container soft delete.

See [routine deployment commands](routine-work.md#deployment),
[current backlog](development-roadmap.md), and [backup/restore evidence](backup-restore.md).

The application, database and network resources are deployed in Central India. Azure rejected affordable Container Registry SKUs for this subscription, so the deployment uses registry-free App Service ZIP publishing.

Public paper-environment URLs:

- `https://sarthico.com`
- `https://www.sarthico.com`
- Azure fallback: `https://sarthitradingweb5amvyarepxaci.azurewebsites.net`

## Cost boundary

The Azure budget is an alert, not a hard spending cap. Before running the workflow, confirm current prices in the Azure Pricing Calculator. The main recurring items are PostgreSQL Flexible Server, its storage/backups and the Linux Basic B1 App Service plan. High availability and geo-redundant backup are disabled for this pre-production environment.

## One-time Azure/GitHub setup

1. Create a resource group in Central India.
2. Create a Microsoft Entra application/workload identity limited to that resource group and federated to the GitHub `azure-paper` environment.
3. Add GitHub environment secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, and `POSTGRES_ADMINISTRATOR_PASSWORD`.
4. Add GitHub environment variable `AZURE_RESOURCE_GROUP`.
5. Protect the `azure-paper` environment with a required reviewer.
6. For subsequent authorized releases use `scripts/tasks.ps1 deploy -Ref main`; include the
   recorder only when required. The helper requires successful main CI and an exact checked package.

The workflow uses OpenID Connect; it does not store an Azure client secret. The database password must be a unique random value of at least 16 characters and must never be committed.

## Database migration and administrator bootstrap

Database initialization/migration is controlled by the release workflow and startup settings;
it is not an operator instruction to run migrations manually against production. Inspect the
workflow's package, schema/readiness and rollback gates before intervention. Administrator
bootstrap remains a one-time explicit operation, not part of a recovery drill.

## Domain setup

GoDaddy DNS and Azure App Service are configured as follows:

- Apex A records: `20.192.171.19` and `20.192.170.138`
- Apex ownership TXT: `asuid` with the Azure Custom Domain Verification ID
- `www` CNAME: `sarthitradingweb5amvyarepxaci.azurewebsites.net`
- Azure App Service managed certificates with SNI SSL for both hostnames

Both custom hostnames were verified through their public HTTPS endpoints. Mailgun MX, SPF and `email` records, GoDaddy nameservers, and Domain Connect records were preserved.

## Recovery and operational boundaries

- Keep one Groww recorder owner; website Groww capture remains off after successful handover.
  The independent website Upstox collector uses an exclusive renewable lease.
- Do not restart/deploy either host during capture. Preserve checked-package identity and fallback.
- Restoring database rows alone is insufficient: encrypted broker state also needs the matching
  protected key ring; replay needs manifests, quotes, policy builds and settings receipts.
- Azure PostgreSQL currently retains seven days of backups; geo-redundant backup/HA are not enabled.
  Replay storage is LRS, not an independently verified cross-region disaster-recovery solution.
- No production failover or paid restore resource is authorized by the current no-cost drill.
