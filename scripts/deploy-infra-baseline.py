"""Fail closed to the existing Bicep deployment. Never log settings or Azure responses."""
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def run(*args):
    return subprocess.run(args, capture_output=True, text=True, check=True, timeout=45).stdout.strip()


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def resource(rid, version):
    return json.loads(run("az", "resource", "show", "--ids", rid, "--api-version", version, "-o", "json"))


def select(value, *keys):
    return {k: value.get(k) for k in keys}


def observe(outputs):
    group = os.environ["RESOURCE_GROUP"]
    app, server = outputs["webAppName"], outputs["postgresServerName"]
    database, login, host = (outputs[k] for k in ("postgresDatabaseName","postgresAdministratorLogin","postgresHostName"))
    if not all(re.fullmatch(r"[A-Za-z0-9._-]+", x) for x in (app,server,database,login,host)):
        raise ValueError("invalid resource names")
    rg = run("az", "group", "show", "-n", group, "--query", "id", "-o", "tsv")
    base = rg + "/providers/"
    site = base + "Microsoft.Web/sites/" + app
    pg = base + "Microsoft.DBforPostgreSQL/flexibleServers/" + server
    dns = base + "Microsoft.Network/privateDnsZones/privatelink.postgres.database.azure.com"
    requests = [
        (site,"2024-11-01"), (site+"/config/web","2024-11-01"),
        (base+"Microsoft.Web/serverfarms/sarthitrading-linux-b1","2024-11-01"),
        (pg,"2024-08-01"), (pg+"/databases/"+database,"2024-08-01"),
        (base+"Microsoft.Network/virtualNetworks/sarthitrading-vnet","2024-05-01"),
        (dns,"2024-06-01"), (dns+"/virtualNetworkLinks/sarthitrading-postgres-link","2024-06-01")]
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        items = list(pool.map(lambda item: resource(*item), requests))
    for item, (rid, _) in zip(items, requests):
        if item["id"].lower() != rid.lower() or item["properties"].get("provisioningState","Succeeded").lower() != "succeeded":
            raise ValueError("resource not ready")
    web, config, plan, postgres, db, network, zone, link = [i["properties"] for i in items]
    if web["state"] != "Running" or postgres["state"] != "Ready" or postgres["fullyQualifiedDomainName"] != host:
        raise ValueError("app or database unavailable")
    # Do not publish or hash credentials. A rotated password/settings mismatch requires Bicep.
    connection = run("az","webapp","config","appsettings","list","-g",group,"-n",app,
                     "--query","[?name=='ConnectionStrings__TradingDatabase'].value | [0]","-o","tsv")
    password = os.environ["PG_ADMIN_PASSWORD"]
    if not password or connection != f"Host={host};Port=5432;Database={database};Username={login};Password={password};SSL Mode=Require;Trust Server Certificate=false":
        raise ValueError("connection settings changed")
    projection = [rg, [select(i,"id","kind","location") for i in items],
        select(web,"serverFarmId","httpsOnly","virtualNetworkSubnetId"),
        select(config,"alwaysOn","ftpsState","healthCheckPath","http20Enabled","linuxFxVersion","minTlsVersion"),
        items[2]["sku"], select(plan,"reserved"),
        items[3]["sku"], select(postgres,"version","administratorLogin","availabilityZone","network"),
        select(postgres["storage"],"storageSizeGB","autoGrow"),
        select(postgres["backup"],"backupRetentionDays","geoRedundantBackup"),postgres["highAvailability"]["mode"],
        select(db,"charset","collation"),network["addressSpace"],
        sorted([(s["name"],select(s.get("properties",s),"addressPrefix","addressPrefixes"),
                 sorted(d.get("properties",d)["serviceName"] for d in s.get("properties",s).get("delegations",[])))
                for s in network["subnets"]]), select(link,"registrationEnabled","virtualNetwork")]
    return {"outputs":outputs, "observed":digest(projection),
            "source":run("git","rev-parse","HEAD:infra","HEAD:.github/workflows/azure-deploy.yml",
                         "HEAD:scripts/deploy-infra-baseline.py","HEAD:scripts/check-website-schema-ready.py")}


def main():
    mode, path = sys.argv[1:3]
    if mode == "capture":
        outputs = {k:v["value"] for k,v in json.loads(Path(sys.argv[3]).read_text()).items()
                   if k in {"webAppName","postgresServerName","postgresHostName","postgresDatabaseName","postgresAdministratorLogin"}}
        Path(path).write_text(json.dumps(observe(outputs)))
    elif mode == "reuse":
        previous = json.loads(Path(path).read_text())
        if observe(previous["outputs"]) != previous:
            raise ValueError("baseline mismatch")
        with open(os.environ["GITHUB_OUTPUT"],"a") as out:
            for k,v in previous["outputs"].items(): print(f"{k}={v}",file=out)
            print("reuse=true",file=out)
    else:
        raise ValueError("unknown mode")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Never print a subprocess invocation or exception details containing credentials.
        print(f"Infrastructure proof unavailable ({type(error).__name__}). Full deployment required.",file=sys.stderr)
        sys.exit(1)
