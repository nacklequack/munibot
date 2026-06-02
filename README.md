# Munibot

Munibot is a LibreMetaverse-based Second Life automation service. It logs in as one configured Second Life account, keeps that session online, and exposes a typed HTTP API for common bot operations such as group roster checks, avatar resolution, messaging, inventory delivery, texture upload, wallet operations, object interaction, and estate list management.

Munibot is intended to run behind private networking and token-based service authentication. Do not expose it directly to the public internet.

## Setup

Copy the sample config and fill in bot credentials:

```powershell
Copy-Item .\config.example.yaml .\config.yaml
```

Then run:

```powershell
dotnet run --project .\src\Munibot\Munibot.csproj -- --config .\config.yaml --urls http://127.0.0.1:5107
```

For a short login smoke test that exits automatically, set:

```yaml
runtime:
  exit_after_login_seconds: 30
```

Do not commit `config.yaml`; it contains credentials.

## Container

Build locally:

```powershell
docker build -t munibot .
```

Run with a mounted YAML config:

```powershell
docker run --rm -p 5107:5107 `
  -v ${PWD}\config.yaml:/app/config/config.yaml:ro `
  munibot
```

The container listens on `http://0.0.0.0:5107` and reads `/app/config/config.yaml` by default.

The GitHub Actions workflow publishes the image to GitHub Container Registry as:

```text
ghcr.io/<owner>/<repo>:<tag>
```

## Authentication

If `tokens` are configured in `config.yaml`, include one of:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/ready `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

```powershell
Invoke-RestMethod http://127.0.0.1:5107/ready `
  -Headers @{ Authorization = "Bearer <token>" }
```

If no tokens are configured, API calls are allowed for local development only.

## Diagnostics

Munibot logs API calls and Second Life events to stdout. API body logging is disabled by default; when enabled, small JSON bodies are logged with token, password, payment, description, texture, and large payload fields redacted. Probe requests to `/health` and `/ready` are not logged at info level.

After login, Munibot sends a lightweight Second Life `AgentUpdate` on `runtime.movement_keepalive_seconds` so the simulator circuit does not sit idle. The default is 20 seconds; set it to `0` only for debugging.

Readiness is available at:

```text
GET /ready
```

Health is available at:

```text
GET /health
```

## Bot Utilities

Check the bot location:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/bot/location `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Teleport the bot to a region:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/bot/teleport `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"region":"Example Region","position":{"x":128,"y":128,"z":25}}'
```

Send an instant message:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/ims `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"avatarId":"00000000-0000-0000-0000-000000000000","message":"Hello from Munibot"}'
```

Send local chat from the bot's current location:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/local-chat `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"message":"Hello from Munibot","channel":0,"chatType":"normal"}'
```

Scan nearby visible objects around the bot:

```powershell
Invoke-RestMethod "http://127.0.0.1:5107/api/objects/nearby?radius=5&name=chair" `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Touch or sit on a visible object by UUID:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/objects/<object-uuid>/interactions `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"action":"touch"}'
```

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/objects/<object-uuid>/interactions `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"action":"sit","sitOffset":{"x":0,"y":0,"z":0}}'
```

## Groups And Avatars

Read a group roster:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/members `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Check a targeted group membership:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/members/<avatar-uuid> `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Manage group bans, invites, ejects, and roles:

```text
GET    /api/groups/<group-uuid>/bans
POST   /api/groups/<group-uuid>/bans/<avatar-uuid>:remove
POST   /api/groups/<group-uuid>/invites
POST   /api/groups/<group-uuid>/members/<avatar-uuid>:eject
GET    /api/groups/<group-uuid>/members/<avatar-uuid>/roles
GET    /api/groups/<group-uuid>/roles
POST   /api/groups/<group-uuid>/roles/<role-uuid>/members/<avatar-uuid>
DELETE /api/groups/<group-uuid>/roles/<role-uuid>/members/<avatar-uuid>
```

Resolve or search avatars:

```text
POST /api/avatars/resolve-keys
POST /api/avatars/resolve-names
GET  /api/avatars/search?query=<name>
```

## Inventory And Textures

Look up inventory:

```text
GET /api/inventory/items/<item-uuid>
GET /api/inventory/items/by-path?path=<inventory-path>
```

Give an inventory item to an avatar:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/inventory/give `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"avatarId":"<avatar-uuid>","itemId":"<item-uuid>","doEffect":true}'
```

Rez an object inventory item into the bot's current simulator, or teleport first when `region` is supplied:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/inventory/rez `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"itemPath":"My Inventory/Objects/Example Object","region":"Example Region","position":{"x":128,"y":128,"z":25},"count":1,"confirmRez":true}'
```

Upload a texture asset into the bot's Textures folder:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/textures `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"name":"Example Texture","description":"Uploaded by Munibot","textureDataBase64":"<sl-ready-jpeg2000-base64>","confirmUploadFee":true}'
```

Texture upload requires `confirmUploadFee: true` because Second Life may charge the bot account's upload fee depending on account benefits. Wallet/balance events are the source of truth for whether L$ were actually deducted.

## Wallet

Fetch the current in-world Linden balance:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/wallet/balance `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Issue an outgoing avatar payment:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/wallet/pay-avatar `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"avatarId":"<avatar-uuid>","amount":1,"description":"Munibot payment test","confirmPayment":true}'
```

Fetch historical Second Life account transactions for the configured bot web account:

```powershell
Invoke-RestMethod "http://127.0.0.1:5107/api/wallet/account-history?fromUtc=2026-01-01T00:00:00Z&toUtc=2026-01-02T00:00:00Z" `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Munibot can also forward live Second Life money events to a configured callback endpoint. The `munibase.wallet_events` config section name is retained for compatibility with existing deployments:

```yaml
munibase:
  wallet_events:
    endpoint_url: https://example.com/webhooks/second-life-money
    shared_secret: replace-with-callback-secret
    timeout_seconds: 10
    max_delivery_attempts: 3
    retry_delay_seconds: 2
```

The posted payload is form-encoded and intended for server-to-server ingestion, matching, dedupe, and audit workflows.

## Estate Security

Estate list operations teleport the bot to an anchor region first so Second Life applies the correct estate context. The bot account must have the appropriate estate manager powers.

```text
GET  /api/estate/allow?anchorRegion=<region-name>
GET  /api/estate/ban?anchorRegion=<region-name>
POST /api/estate/allow/<avatar-uuid>
POST /api/estate/allow/<avatar-uuid>:remove
POST /api/estate/ban/<avatar-uuid>
POST /api/estate/ban/<avatar-uuid>:remove
```

## Development

Run tests:

```powershell
dotnet test .\Munibot.slnx
```

No public license has been selected yet. Until a license is added, this repository is visible source with all rights reserved by the repository owner.
