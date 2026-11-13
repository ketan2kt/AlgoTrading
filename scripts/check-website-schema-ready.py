"""Allow an app-only release to skip two migration-mode restarts, fail closed."""
import json
import os
import re
import subprocess
import time
import urllib.error
import urllib.request


def safety_settings_are_off(group, app):
    for key in ("DatabaseInitialization__ApplyMigrations", "PaperTradeReset__Enabled"):
        value = subprocess.run(
            ["az", "webapp", "config", "appsettings", "list", "-g", group, "-n", app,
             "--query", f"[?name=='{key}'].value | [0]", "-o", "tsv"],
            check=True, capture_output=True, text=True, timeout=20,
        ).stdout.strip()
        if value.lower() != "false":
            return False
    return True


def stable_schema_ready(verify, sleep=time.sleep, attempts=12):
    consecutive = 0
    for attempt in range(attempts):
        consecutive = consecutive + 1 if verify() else 0
        if consecutive >= 2:
            return True
        if attempt + 1 < attempts:
            sleep(5)
    return False


def verify_package(app, sha):
    base = f"https://{app}.azurewebsites.net"
    expected = "1.0.0+" + sha
    try:
        with urllib.request.urlopen(base + "/api/system/status", timeout=8) as response:
            version = json.load(response).get("buildVersion")
        if version not in (expected, f"{expected}.{sha}"):
            return False
        # The same exact deployed binary's readiness gate queries PostgreSQL
        # for pending EF migrations. Connectivity alone is not sufficient.
        with urllib.request.urlopen(base + "/health/ready", timeout=8) as response:
            return response.status == 200
    except (OSError, ValueError, AttributeError, urllib.error.URLError):
        return False


def main():
    app, group, sha = (os.environ.get(key, "") for key in ("APP", "RESOURCE_GROUP", "GITHUB_SHA"))
    if not re.fullmatch(r"[a-z0-9-]+", app) or not re.fullmatch(r"[0-9a-f]{40}", sha) or not group:
        return 1
    try:
        return 0 if safety_settings_are_off(group, app) and stable_schema_ready(lambda: verify_package(app, sha)) else 1
    except (OSError, subprocess.SubprocessError):
        # Never log a response, command invocation, or environment value.
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
