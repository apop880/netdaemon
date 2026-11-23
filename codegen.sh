#!/bin/sh
set -e

PROD_SECRETS_FILE="/mnt/hass_config/netdaemon6/appsettings.Production.json"
if [ ! -f "$PROD_SECRETS_FILE" ]; then
  echo "❌ Missing $PROD_SECRETS_FILE" >&2
  exit 1
fi

HomeAssistant__Token=$(jq -r '.HomeAssistant.Token' "$PROD_SECRETS_FILE") nd-codegen "$@"
