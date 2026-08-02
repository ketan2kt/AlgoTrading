# Azure paper deployment

## Promotion status

This deployment is a **monitoring/demo paper environment only**. Durable paper-broker order/position recovery is implemented, but unattended paper trading remains prohibited until the remaining Phase 7 audit, fee/slippage, replay, and soak-test gates pass. Live mode is rejected by application startup validation and is not accepted by the Bicep template.

## Architecture

- Azure App Service Linux Basic B1, one instance
- Azure Database for PostgreSQL Flexible Server, Burstable `Standard_B1ms`, PostgreSQL 16, 32 GiB storage, seven-day backup retention
- GitHub Actions ZIP deployment; no container registry is required
- Log Analytics with 30-day retention
- VNet-integrated App Service
- PostgreSQL on a delegated subnet with public access disabled and private DNS
- Angular production files served by ASP.NET Core from the same container and origin

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
6. Run **Azure paper deployment** manually and type `DEPLOY-PAPER`.

The workflow uses OpenID Connect; it does not store an Azure client secret. The database password must be a unique random value of at least 16 characters and must never be committed.

## Database migration and administrator bootstrap

The application intentionally does not migrate the database automatically. The initial checked-in EF Core migration was applied through the controlled Azure workflow and automatic migration was disabled afterward. The administrator was created through the one-time bootstrap workflow; its temporary App Service settings and GitHub bootstrap-password secret were removed after successful login verification.

## Domain setup

GoDaddy DNS and Azure App Service are configured as follows:

- Apex A records: `20.192.171.19` and `20.192.170.138`
- Apex ownership TXT: `asuid` with the Azure Custom Domain Verification ID
- `www` CNAME: `sarthitradingweb5amvyarepxaci.azurewebsites.net`
- Azure App Service managed certificates with SNI SSL for both hostnames

Both custom hostnames were verified through their public HTTPS endpoints. Mailgun MX, SPF and `email` records, GoDaddy nameservers, and Domain Connect records were preserved.

## Known limitations

- Paper broker recovery assumes the configured single App Service instance; concurrent journal writers are not supported yet.
- Subsequent EF migrations remain controlled manual operations and must not run automatically at application startup.
- No Groww token is provisioned, and no live/order-capable gateway exists.
