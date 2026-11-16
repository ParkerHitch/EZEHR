#!/usr/bin/env bash
# Runs EZEHR on this machine (the NUC) from this checkout, as a systemd service, at https://<machine>.<tailnet>.ts.net.
# Run it from the checkout, as your own user with sudo:
#
#   sudo scripts/nuc-setup.sh
#
# Run it again after `git pull` to rebuild and restart. It creates only what's missing: the TLS certificate, the
# secrets, the SQL Server container and the tailnet port for SQL.
#
# Kestrel serves HTTPS itself, on this machine's Tailscale address, with a `tailscale cert` certificate. No proxy:
# the network door needs each organization's client certificate on the TLS connection, which a proxy would end.
# SQL Server listens on 127.0.0.1 only; `tailscale serve --tcp` passes tailnet port 1433 to it, for the laptop's
# tools (demo reset, demo status, stage's lookup of Sam). Needs: the .NET 11 SDK, docker, and HTTPS (Serve)
# enabled on the tailnet.

# Stop at the first failing command (-e), on any unset variable (-u), and when any command in a pipe fails.
set -euo pipefail

# ==== Who's running this, and where ====

# Root is needed for /etc, systemd and the certificate; SUDO_USER says which real user ran sudo.
if [[ $EUID -ne 0 || -z "${SUDO_USER:-}" ]]; then
  # Tell them how to run it instead.
  echo "run it as your own user with sudo: sudo $0" >&2
  # Give up without changing anything.
  exit 1
fi
# Move to the repo root (this script lives in scripts/), so relative paths below mean the checkout.
cd "$(dirname "$0")/.."
# Remember the checkout's absolute path: the service runs the build from here.
REPO_DIR=$(pwd)
# The service runs as you, the checkout's owner, so it can read the checkout in your home directory.
RUN_AS=$SUDO_USER
# Your home directory, looked up from the user database (sudo may have changed $HOME).
RUN_AS_HOME=$(getent passwd "$RUN_AS" | cut -d: -f6)
# Where the service's settings, secrets and TLS files live.
CONFIG_DIR=/etc/ezehr
# The name of the SQL Server docker container.
CONTAINER=ezehr-sql
# Dr. Rivera's Entra tenant: Riverside's clinicians sign in through it.
RIVERSIDE_TENANT_ID=fd4e23f3-7ba3-49f1-bdcd-3f3c55d42f0d

# ==== Check the tools this needs ====

# For each program the script calls...
for tool in tailscale docker curl; do
  # ...stop with a clear message if it isn't installed.
  command -v "$tool" >/dev/null || { echo "$tool isn't installed" >&2; exit 1; }
done
# Prefer a per-user .NET install (what dotnet-install.sh creates)...
DOTNET="$RUN_AS_HOME/.dotnet/dotnet"
# ...and fall back to whatever `dotnet` is on the PATH.
[[ -x "$DOTNET" ]] || DOTNET=$(command -v dotnet || true)
# The project targets net11.0, so a .NET 11 SDK must be among the installed SDKs.
if [[ -z "$DOTNET" ]] || ! sudo -u "$RUN_AS" "$DOTNET" --list-sdks | grep -q '^11\.'; then
  # Say what's missing and where it looked.
  echo "the .NET 11 SDK isn't installed for $RUN_AS (looked in ~/.dotnet and PATH)" >&2
  # Give up before building.
  exit 1
fi

# ==== Values the setup needs ====

# This machine's Tailscale IPv4 address (100.x.y.z): Kestrel listens only there, so the LAN can't reach it.
TAILSCALE_IP=$(tailscale ip -4)
# This machine's tailnet name, e.g. colin-nuc.tail51bf60.ts.net. The first DNSName in the status JSON is this
# machine's own ("Self" comes before the peers); cut takes the quoted value and sed drops the trailing dot.
DNS_NAME=$(tailscale status --json | grep -m1 '"DNSName"' | cut -d'"' -f4 | sed 's/\.$//')
# Reads one string value out of appsettings.Development.json, so the NUC uses the same values as the laptop.
json_value() {
  # Find `"<key>": "<value>"` and print the fourth quote-delimited field, the value.
  grep -o "\"$1\": *\"[^\"]*\"" appsettings.Development.json | cut -d'"' -f4
}
# The SHA-256 thumbprint of .pki/platform.pfx: only that certificate may call register-provider.
PLATFORM_THUMBPRINT=$(json_value PlatformCertificateThumbprint)
# The Entra app registration Dr. Rivera signs in through.
ENTRA_CLIENT_ID=$(json_value ClientId)
# Create /etc/ezehr if it isn't there yet.
mkdir -p "$CONFIG_DIR"

# ==== TLS certificate ====

# Get a certificate when there's none yet, or when it's over 60 days old (Let's Encrypt certificates last 90).
if [[ ! -f "$CONFIG_DIR/tls.crt" ]] || [[ -n $(find "$CONFIG_DIR/tls.crt" -mtime +60) ]]; then
  # Tailscale gets a Let's Encrypt certificate for the tailnet name and writes the certificate and its key.
  tailscale cert --cert-file "$CONFIG_DIR/tls.crt" --key-file "$CONFIG_DIR/tls.key" "$DNS_NAME"
fi
# Root owns both files; your group lets the service (running as you) read them.
chown root:"$RUN_AS" "$CONFIG_DIR/tls.crt" "$CONFIG_DIR/tls.key"
# The certificate is public: anyone may read it.
chmod 644 "$CONFIG_DIR/tls.crt"
# The private key: root writes it, your group reads it, nobody else sees it.
chmod 640 "$CONFIG_DIR/tls.key"

# ==== Secrets (made once) ====

# Only on the first run: later runs reuse them, or existing records would become unreadable.
if [[ ! -f "$CONFIG_DIR/secrets.env" ]]; then
  # 32 random bytes, base64: the key that wraps every patient's data key (stands in for Key Vault).
  key_encryption_key=$(head -c 32 /dev/urandom | base64)
  # A random letters-and-digits password, with a symbol, a digit and both cases, as SQL Server requires.
  sql_password="Ez-$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)9a"
  # In a subshell, so the stricter umask doesn't leak: the file is created readable by root only (600).
  (umask 077 && printf 'KEY_ENCRYPTION_KEY=%s\nSQL_PASSWORD=%s\n' "$key_encryption_key" \
    "$sql_password" > "$CONFIG_DIR/secrets.env")
  # Say where they went, and why the file matters.
  echo "wrote $CONFIG_DIR/secrets.env (back it up: without it the records can't be decrypted)"
fi
# Load KEY_ENCRYPTION_KEY and SQL_PASSWORD into this script.
# shellcheck source=/dev/null
source "$CONFIG_DIR/secrets.env"

# ==== SQL Server ====

# Runs one SQL statement inside the container: database $2, or master by default.
sql() {
  # sqlcmd ships in the image. -C trusts the container's own certificate, -b fails on SQL errors, and
  # QUOTED_IDENTIFIER is on because updates to tables with filtered indexes (organizations) need it.
  docker exec "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SQL_PASSWORD" \
    -d "${2:-master}" -b -Q "SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; $1"
}
# Create the container only if it doesn't exist yet (running or stopped).
if ! docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
  # Start SQL Server 2022: restart it with docker, accept the licence, set the sa password...
  docker run -d --name "$CONTAINER" --restart unless-stopped \
    -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$SQL_PASSWORD" \
    -p 127.0.0.1:1433:1433 -v ezehr-sql:/var/opt/mssql \
    mcr.microsoft.com/mssql/server:2022-latest >/dev/null
  # ...listen on this machine's loopback only, and keep the data in the ezehr-sql volume across restarts.
  echo "started SQL Server ($CONTAINER)"
fi
# Make sure it's running, in case it was stopped.
docker start "$CONTAINER" >/dev/null
# SQL Server takes a few seconds to accept logins after it starts.
echo "waiting for SQL Server..."
# Try a trivial query every 2 seconds until one works.
until sql "SELECT 1" >/dev/null 2>&1; do sleep 2; done
# Create the ezehr database if it's missing. The app creates its tables when it starts.
sql "IF DB_ID('ezehr') IS NULL CREATE DATABASE ezehr"

# ==== SQL for the laptop, over the tailnet only ====

# Tailscale forwards port 1433 on this machine's tailnet address to the container, in the background. Only
# tailnet devices reach it, never the internet; Tailscale remembers it across restarts.
tailscale serve --bg --tcp 1433 tcp://127.0.0.1:1433 >/dev/null

# ==== The app's settings ====

# Rewritten every run, so a changed thumbprint or client id takes effect. Root-only (600): it holds the secrets.
(umask 077 && cat > "$CONFIG_DIR/ezehr.env" <<EOF
ASPNETCORE_ENVIRONMENT=Production
DOTNET_hostBuilder__reloadConfigOnChange=false
Kestrel__Endpoints__Https__Url=https://$TAILSCALE_IP:443
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
# What those settings do, in order:
#   Production                 no developer error pages; settings come from this file, not appsettings.Development
#   reloadConfigOnChange=false don't watch the config files (avoids running out of inotify watches)
#   Kestrel Url                serve HTTPS on the Tailscale address, port 443
#   Certificate Path/KeyPath   the tailscale cert files from above
#   App__Origin                the site's address: passkeys, invite links and QR codes use it
#   KeyWrapper Local + key     wrap patient keys with the key from secrets.env (Key Vault went with Azure)
#   PlatformCertificate...     the only certificate allowed to call register-provider
#   AzureAd                    Entra sign-in for clinicians, any work tenant, through the EZEHR app registration
#   AZURE_SQL_CONNECTIONSTRING the container, over loopback

# ==== Build and run ====

# Stop the running service, if any, before replacing its build (ignore the error the first time).
systemctl stop ezehr 2>/dev/null || true
# Build the checkout in Release, as you, so bin/ and obj/ stay yours. -v q keeps the output short.
sudo -u "$RUN_AS" "$DOTNET" build EZEHR.csproj -c Release -nologo -v q
# Write the systemd unit (overwritten each run, so it always points at this checkout).
cat > /etc/systemd/system/ezehr.service <<EOF
[Unit]
Description=EZEHR ($REPO_DIR)
After=network-online.target tailscaled.service docker.service
Wants=network-online.target

[Service]
User=$RUN_AS
WorkingDirectory=$REPO_DIR
ExecStart=$DOTNET $REPO_DIR/bin/Release/net11.0/EZEHR.dll
EnvironmentFile=$CONFIG_DIR/ezehr.env
AmbientCapabilities=CAP_NET_BIND_SERVICE
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
# What the unit says, in order:
#   After/Wants        start after the network, Tailscale and docker
#   User               run as you
#   WorkingDirectory   the checkout, which is the app's content root (Security/Trust/*.pem, wwwroot)
#   ExecStart          run the Release build with this machine's dotnet
#   EnvironmentFile    load the settings written above
#   AmbientCapabilities allow port 443 without running as root
#   Restart/RestartSec if it exits (e.g. at boot, before Tailscale's address exists), start again after 5 seconds
#   WantedBy           start at boot

# Make systemd read the new or changed unit file.
systemctl daemon-reload
# Start the service at every boot.
systemctl enable ezehr >/dev/null 2>&1
# Start it now (or restart it on the new build).
systemctl restart ezehr

# ==== Wait for the site ====

# Say what we're waiting for.
echo "waiting for https://$DNS_NAME ..."
# curl prints 000 when it couldn't connect at all.
status=000
# Try for up to 2 minutes (60 tries, 2 seconds apart).
for _ in $(seq 1 60); do
  # Ask for the home page and keep only the HTTP status. Any real status means Kestrel is answering.
  status=$(curl -s -o /dev/null -w '%{http_code}' "https://$DNS_NAME/" || true)
  # Stop waiting as soon as it answers.
  [[ "$status" != "000" ]] && break
  # Otherwise wait and try again.
  sleep 2
done
# Still nothing after 2 minutes: the app failed to start.
if [[ "$status" == "000" ]]; then
  # Point at the service's log, which says why.
  echo "the site didn't come up; see: journalctl -u ezehr -n 50" >&2
  # Stop here.
  exit 1
fi

# ==== Riverside's tenant ====

# The app has made its tables (and seeded the organizations) by now. Map Riverside to Dr. Rivera's tenant so the
# sign-in is accepted. demo reset leaves this row alone, so setting it again is harmless.
sql "UPDATE organizations SET entra_tenant_id = '$RIVERSIDE_TENANT_ID' WHERE uri = 'urn:provider:riverside'" ezehr

# ==== Hand the SQL password to the laptop ====

# Make sure .pki/ exists in the checkout, owned by you and private (it's git-ignored).
install -d -m 700 -o "$RUN_AS" .pki
# Create an empty file there, owned by you, readable only by you...
install -m 600 -o "$RUN_AS" /dev/null .pki/nuc-sql-password
# ...and write the password into it, for scripts/nuc.env on the laptop.
printf '%s' "$SQL_PASSWORD" > .pki/nuc-sql-password

# Done: say where the site is...
echo "EZEHR is up at https://$DNS_NAME"
# ...and how to point the laptop's tools at it.
echo "On the laptop: scp $(hostname):$REPO_DIR/.pki/nuc-sql-password .pki/  then  source scripts/nuc.env"
