#!/bin/sh
set -e # Exit immediately if a command exits with a non-zero status.

echo "🚀 Building to /mnt/hass_config/netdaemon5..."

# Build the project
dotnet publish -c Release -o /mnt/hass_config/netdaemon5

# Restart the add-on
echo "🔄 Restarting Netdaemon add-on..."
APPSETTINGS_FILE="appsettings.json"
HASS_BASE_URL=$(jq -r '.HomeAssistant.Ssl' "$APPSETTINGS_FILE" | grep -qi true && echo "https://$(jq -r '.HomeAssistant.Host' "$APPSETTINGS_FILE"):$(jq -r '.HomeAssistant.Port' "$APPSETTINGS_FILE")" || echo "http://$(jq -r '.HomeAssistant.Host' "$APPSETTINGS_FILE"):$(jq -r '.HomeAssistant.Port' "$APPSETTINGS_FILE")")
HASS_TOKEN=$(jq -r '.HomeAssistant.Token' "$APPSETTINGS_FILE")

curl -X POST \
  -H "Authorization: Bearer $HASS_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"addon": "c6a2317c_netdaemon5"}' \
  "$HASS_BASE_URL/api/services/hassio/addon_restart"

echo "✅ Netdaemon add-on restarted."
