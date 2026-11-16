#!/usr/bin/env bash
# Runs EZEHR on this machine (the NUC) from this checkout, as a systemd service, at https://<machine>.<tailnet>.ts.net,
# open to the internet through Tailscale Funnel.
# Run it from the checkout, as your own user with sudo:
#
#   sudo scripts/nuc-setup.sh
#
# Run it again after `git pull` to republish and restart; CI does both on every push to main (scripts/nuc-deploy.sh). It creates only what's missing: the TLS certificate, the
# secrets, the SQL Server container and the tailnet port for SQL.
#
# Kestrel serves HTTPS itself, on 127.0.0.1:8443, with a `tailscale cert` certificate. `tailscale funnel --tcp 443`
# passes raw TCP from the internet (and the tailnet) to it without decrypting, because the network door needs each
# organization's client certificate on the TLS connection, which a TLS-terminating proxy would end. So every caller
# reaches Kestrel from 127.0.0.1. SQL Server listens on 127.0.0.1 only; `tailscale serve --tcp` passes tailnet port
# 1433 to it, for the laptop's tools (demo reset, demo status, stage's lookup of Sam), and never the internet.
# Needs: the .NET 11 SDK, docker, and HTTPS and Funnel enabled on the tailnet (the first funnel run prints a link
# to turn it on).
set -euo pipefail

if [[ $EUID -ne 0 || -z "${SUDO_USER:-}" ]]; then
  echo "run it as your own user with sudo: sudo $0" >&2
  exit 1
fi
cd "$(dirname "$0")/.."
REPO_DIR=$(pwd)
RUN_AS=$SUDO_USER
RUN_AS_HOME=$(getent passwd "$RUN_AS" | cut -d: -f6)
CONFIG_DIR=/etc/ezehr
CONTAINER=ezehr-sql
# Dr. Rivera's Entra tenant: Riverside's clinicians sign in through it.
RIVERSIDE_TENANT_ID=fd4e23f3-7ba3-49f1-bdcd-3f3c55d42f0d

for tool in tailscale docker curl; do
  command -v "$tool" >/dev/null || { echo "$tool isn't installed" >&2; exit 1; }
done
DOTNET="$RUN_AS_HOME/.dotnet/dotnet"
[[ -x "$DOTNET" ]] || DOTNET=$(command -v dotnet || true)
if [[ -z "$DOTNET" ]] || ! sudo -u "$RUN_AS" "$DOTNET" --list-sdks | grep -q '^11\.'; then
  echo "the .NET 11 SDK isn't installed for $RUN_AS (looked in ~/.dotnet and PATH)" >&2
  exit 1
fi

# The first DNSName in the status is this machine's own ("Self" comes before the peers).
DNS_NAME=$(tailscale status --json | grep -m1 '"DNSName"' | cut -d'"' -f4 | sed 's/\.$//')
# The same values the laptop's Development run uses, so the two never drift.
json_value() {
  grep -o "\"$1\": *\"[^\"]*\"" appsettings.Development.json | cut -d'"' -f4
}
PLATFORM_THUMBPRINT=$(json_value PlatformCertificateThumbprint)
ENTRA_CLIENT_ID=$(json_value ClientId)
mkdir -p "$CONFIG_DIR"

# --- TLS certificate for the tailnet name (Let's Encrypt, 90 days). Renewed when it's over 60 days old.
if [[ ! -f "$CONFIG_DIR/tls.crt" ]] || [[ -n $(find "$CONFIG_DIR/tls.crt" -mtime +60) ]]; then
  tailscale cert --cert-file "$CONFIG_DIR/tls.crt" --key-file "$CONFIG_DIR/tls.key" "$DNS_NAME"
fi
chown root:"$RUN_AS" "$CONFIG_DIR/tls.crt" "$CONFIG_DIR/tls.key"
chmod 644 "$CONFIG_DIR/tls.crt"
chmod 640 "$CONFIG_DIR/tls.key"

# --- Secrets, made once: the key that wraps every patient's data key, and the SQL password.
if [[ ! -f "$CONFIG_DIR/secrets.env" ]]; then
  key_encryption_key=$(head -c 32 /dev/urandom | base64)
  sql_password="Ez-$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)9a"
  (umask 077 && printf 'KEY_ENCRYPTION_KEY=%s\nSQL_PASSWORD=%s\n' "$key_encryption_key" \
    "$sql_password" > "$CONFIG_DIR/secrets.env")
  echo "wrote $CONFIG_DIR/secrets.env (back it up: without it the records can't be decrypted)"
fi
# shellcheck source=/dev/null
source "$CONFIG_DIR/secrets.env"

# --- SQL Server, on 127.0.0.1 only, with its data in a named volume.
sql() {
  docker exec "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SQL_PASSWORD" \
    -d "${2:-master}" -b -Q "SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; $1"
}
if ! docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
  docker run -d --name "$CONTAINER" --restart unless-stopped \
    -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$SQL_PASSWORD" \
    -e MSSQL_MEMORY_LIMIT_MB=2048 -p 127.0.0.1:1433:1433 \
    -v ezehr-sql:/var/opt/mssql \
    mcr.microsoft.com/mssql/server:2022-latest >/dev/null
  echo "started SQL Server ($CONTAINER)"
fi
docker start "$CONTAINER" >/dev/null
echo "waiting for SQL Server..."
until sql "SELECT 1" >/dev/null 2>&1; do sleep 2; done
sql "IF DB_ID('ezehr') IS NULL CREATE DATABASE ezehr"

# --- Tailnet port 1433 → the container, for the laptop's tools. Tailnet devices only, never the internet.
tailscale serve --bg --tcp 1433 tcp://127.0.0.1:1433 >/dev/null

# --- The app's settings. Rewritten every run, so a changed thumbprint or client id takes effect.
(umask 077 && cat > "$CONFIG_DIR/ezehr.env" <<EOF
ASPNETCORE_ENVIRONMENT=Production
DOTNET_hostBuilder__reloadConfigOnChange=false
Kestrel__Endpoints__Https__Url=https://127.0.0.1:8443
Kestrel__Endpoints__Https__Certificate__Path=$CONFIG_DIR/tls.crt
Kestrel__Endpoints__Https__Certificate__KeyPath=$CONFIG_DIR/tls.key
App__Origin=https://$DNS_NAME
App__KeyWrapper__Mode=Local
App__LocalKekBase64=$KEY_ENCRYPTION_KEY
App__PlatformCertificateThumbprint=$PLATFORM_THUMBPRINT
AzureAd__TenantId=organizations
AzureAd__ClientId=$ENTRA_CLIENT_ID
AZURE_SQL_CONNECTIONSTRING=Server=127.0.0.1,1433;Database=ezehr;User Id=sa;Password=$SQL_PASSWORD;TrustServerCertificate=True
EOF
)

# --- Publish this checkout (on the installed .NET, not self-contained), and run it as a service. Publish, not
# build: MapStaticAssets serves pre-compressed copies (site.css.gz), and only publish puts them next to the app.
# A build leaves them in obj/, where only a Development run looks, so Production served empty gzip responses.
# Published next to the running copy first, so the site is only down for the swap and the app's start, and a failed
# publish (set -e) leaves the old version serving.
PUBLISH_DIR=$REPO_DIR/bin/nuc
sudo -u "$RUN_AS" rm -rf "$PUBLISH_DIR.next" "$PUBLISH_DIR.old"
sudo -u "$RUN_AS" "$DOTNET" publish EZEHR.csproj -c Release -o "$PUBLISH_DIR.next" -nologo -v q
cat > /etc/systemd/system/ezehr.service <<EOF
[Unit]
Description=EZEHR ($REPO_DIR)
After=network-online.target tailscaled.service docker.service
Wants=network-online.target

[Service]
User=$RUN_AS
WorkingDirectory=$PUBLISH_DIR
ExecStart=$DOTNET $PUBLISH_DIR/EZEHR.dll
EnvironmentFile=$CONFIG_DIR/ezehr.env
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable ezehr >/dev/null 2>&1
systemctl stop ezehr
if [[ -d "$PUBLISH_DIR" ]]; then
  mv "$PUBLISH_DIR" "$PUBLISH_DIR.old"
fi
mv "$PUBLISH_DIR.next" "$PUBLISH_DIR"
systemctl start ezehr
rm -rf "$PUBLISH_DIR.old"

# --- Internet (and tailnet) port 443 → Kestrel, as raw TCP so the client certificate reaches it. Not silenced:
# the first run prints the link that enables Funnel, and waits for it.
tailscale funnel --bg --yes --tcp 443 tcp://127.0.0.1:8443

echo "waiting for https://$DNS_NAME ..."
status=000
for _ in $(seq 1 60); do
  status=$(curl -s -o /dev/null -w '%{http_code}' "https://$DNS_NAME/" || true)
  [[ "$status" != "000" ]] && break
  sleep 2
done
if [[ "$status" == "000" ]]; then
  echo "the site didn't come up; see: journalctl -u ezehr -n 50" >&2
  exit 1
fi

# --- The app made the tables on start. demo reset leaves this row alone, so setting it again is harmless.
sql "UPDATE organizations SET entra_tenant_id = '$RIVERSIDE_TENANT_ID' WHERE uri = 'urn:provider:riverside'" ezehr

# --- For the laptop's tools: scripts/nuc.env reads the SQL password from .pki/nuc-sql-password.
install -d -m 700 -o "$RUN_AS" .pki
install -m 600 -o "$RUN_AS" /dev/null .pki/nuc-sql-password
printf '%s' "$SQL_PASSWORD" > .pki/nuc-sql-password

echo "EZEHR is up at https://$DNS_NAME"
echo "On the laptop: scp $(hostname):$REPO_DIR/.pki/nuc-sql-password .pki/  then  source scripts/nuc.env"
