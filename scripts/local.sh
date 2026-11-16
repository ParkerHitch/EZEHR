#!/usr/bin/env bash
# Temporary helper for running EZEHR locally against a throwaway SQL Server container.
# Superseded by the `demo` command (Task 15); don't build on it.
#
#   scripts/local.sh up                       SQL container + database + local certs, then run the server
#   scripts/local.sh reset                    empty the database (restart the server to recreate tables)
#   scripts/local.sh down                     remove the SQL container
#   scripts/local.sh invite <provider> <id>   e.g. invite lakeside LUC-20417
#   scripts/local.sh seed                     push maple, northwind and lakeside (Sam must have linked them)
#   scripts/local.sh riverside                push Riverside's visit documentation
#   scripts/local.sh kidney                   run Kidney Check for Sam
#   scripts/local.sh broker                   broker with no certificate, then a self-made one
#   scripts/local.sh patient                  print Sam's patient id
#   scripts/local.sh audit                    show the audit log
#   scripts/local.sh sql "<query>"            run any query against the local database
set -euo pipefail

cd "$(dirname "$0")/.."
DOTNET=$(command -v dotnet || true)
CONTAINER=ezehr-sql
# Only protects a container bound to 127.0.0.1.
SA_PASSWORD='EzehrLocal-Dev-2026'
export AZURE_SQL_CONNECTIONSTRING="Server=127.0.0.1,1433;Database=ezehr;User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=True"

sql() {
  docker exec "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" \
    -d "${2:-ezehr}" -W -Q "SET NOCOUNT ON; $1"
}

# One value, no headers.
sql_value() {
  docker exec "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" \
    -d ezehr -W -h -1 -Q "SET NOCOUNT ON; $1" | tr -d '[:space:]'
}

tools() {
  "$DOTNET" run --no-build --project EZEHR.Tools -- "$@"
}

patient_id() {
  local id
  id=$(sql_value "SELECT TOP 1 CAST(patient_id AS NVARCHAR(36)) FROM provider_links WHERE provider_uri = 'urn:provider:lakeside' AND provider_patient_id = 'LUC-20417'")
  if [[ -z "$id" ]]; then
    echo "Sam isn't enrolled yet: run 'invite lakeside LUC-20417' and enroll in the browser." >&2
    exit 1
  fi
  echo "$id"
}

case "${1:-}" in
  up)
    if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
      docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
      docker run -d --name "$CONTAINER" -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$SA_PASSWORD" \
        -p 127.0.0.1:1433:1433 mcr.microsoft.com/mssql/server:2022-latest >/dev/null
      echo "waiting for SQL Server..."
      until sql "SELECT 1" master >/dev/null 2>&1; do sleep 2; done
    fi
    sql "IF DB_ID('ezehr') IS NULL CREATE DATABASE ezehr" master
    "$DOTNET" build EZEHR.Tools -v q -nologo | grep -E "error|Warn|Build succeeded" || true
    if [[ ! -f .pki/lakeside.pfx ]]; then
      tools certs issue --ca local
      tools certs broker
    fi
    echo "server: https://localhost:7189  (Ctrl+C to stop)"
    exec "$DOTNET" run --project EZEHR.csproj --launch-profile https
    ;;
  reset)
    sql "DROP DATABASE IF EXISTS ezehr; CREATE DATABASE ezehr" master
    echo "database emptied; restart the server ('up') to recreate the tables"
    ;;
  down)
    docker rm -f "$CONTAINER"
    ;;
  invite)
    tools provider invite "$2" "$3"
    ;;
  seed)
    tools provider push maple MSF-0932
    tools provider push northwind NWH-88213
    tools provider push lakeside LUC-20417
    ;;
  riverside)
    tools provider push riverside RPC-1107
    ;;
  kidney)
    id=$(patient_id)
    tools kidney-check run "$id"
    ;;
  broker)
    id=$(patient_id)
    tools broker request "$id" --cert none
    tools broker request "$id" --cert self
    ;;
  patient)
    patient_id
    ;;
  audit)
    sql "SELECT id, at, action, actor_name, granted, reason, claimed_purpose FROM audit_events ORDER BY id"
    ;;
  sql)
    sql "$2"
    ;;
  *)
    sed -n '2,17p' "$0"
    exit 2
    ;;
esac
