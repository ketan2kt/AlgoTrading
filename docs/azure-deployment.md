# Azure paper deployment

## Promotion status

This deployment is a **monitoring/demo paper environment only**. It must not be used for unattended paper trading until durable paper-order/position persistence and restart reconstruction pass the Phase 7 gate. Live mode is rejected by application startup validation and is not accepted by the Bicep template.

## Architecture

- Azure App Service Linux Basic B1, one instance
- Azure Database for PostgreSQL Flexible Server, Burstable `Standard_B1ms`, PostgreSQL 16, 32 GiB storage, seven-day backup retention
- GitHub Actions ZIP deployment; no container registry is required
- Log Analytics with 30-day retention
- VNet-integrated App Service
- PostgreSQL on a delegated subnet with public access disabled and private DNS
- Angular production files served by ASP.NET Core from the same container and origin

The application, database and network resources default to Central India. Azure rejected affordable Container Registry SKUs for this subscription, so the deployment uses registry-free App Service ZIP publishing. The custom hostname is intended to be `trading.sarthico.com`, but DNS and certificate binding are deliberately separate because the App Service hostname must exist first.

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

The application intentionally does not migrate the database automatically. Before the first application revision is considered ready, run the checked-in EF Core migration from an approved one-off job with private network access. Administrator bootstrap must likewise be a one-time controlled job, followed by removal of all bootstrap settings. These jobs are not automated yet.

## Domain setup

After the App Service is healthy:

1. Add `trading.sarthico.com` as a custom domain on the App Service.
2. Add the Azure-provided DNS verification record and CNAME in GoDaddy.
3. Bind an Azure managed certificate.
4. Verify HTTPS, cookie authentication, antiforgery, SignalR, and health endpoints.

Do not redirect the apex `sarthico.com` until its existing use is confirmed.

## Known limitations

- Paper broker state remains process-local and is lost on restart.
- EF migration and one-time administrator bootstrap require a private-network job design.
- ASP.NET Core Data Protection keys are not yet durable across revisions, so login cookies can be invalidated after deployment.
- No Groww token is provisioned, and no live/order-capable gateway exists.
- GitHub workload identity and environment protection are not configured yet; the deployment workflow cannot run until that one-time setup is complete.
