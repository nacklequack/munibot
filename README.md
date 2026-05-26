# Munibot

Munibot is a small LibreMetaverse-based Second Life bot prototype.

Right now it only proves that we can log in from a YAML config file and keep the session alive. The next useful layer is group roster lookup, but this first step keeps the authentication path tiny and observable.

## Setup

Copy the sample config and fill in bot credentials:

```powershell
Copy-Item .\config.example.yaml .\config.yaml
```

Then run:

```powershell
dotnet run -- --config .\config.yaml
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

## Group roster API

Run the bot and query a group UUID:

```powershell
dotnet run -- --config .\config.yaml --urls http://127.0.0.1:5107
```

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/22222222-2222-4222-8222-222222222222/members
```

For a targeted membership check:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/22222222-2222-4222-8222-222222222222/members/11111111-1111-4111-8111-111111111111
```

If `tokens` are configured in `config.yaml`, include one of:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/members `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/members `
  -Headers @{ Authorization = "Bearer <token>" }
```

## Diagnostics

Munibot logs API calls and Second Life events to stdout. API body logging is disabled by default; when enabled, small JSON bodies are logged with token, password, payment, description, texture, and large payload fields redacted. Probe requests to `/health` and `/ready` are not logged at info level.

After login, Munibot sends a lightweight Second Life `AgentUpdate` on `runtime.movement_keepalive_seconds` so the simulator circuit does not sit idle. The default is 20 seconds; set it to `0` only for debugging.

`ExperienceEvent` generic messages are logged with their raw parameters when Second Life event logging is enabled. To persistently allow a trusted experience for the bot account, configure `experiences.auto_allow` or call:

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:5107/api/experiences/<experience-uuid>:allow `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Readiness is available at:

```text
GET /ready
```

The Corrade replacement roadmap lives in `docs/corrade-replacement-roadmap.md`.

## Bot utilities

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
  -Body '{"avatarId":"11111111-1111-4111-8111-111111111111","message":"Hello from Munibot"}'
```

Send local chat from the bot's current location:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/local-chat `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"message":"Hello from Munibot","channel":0,"chatType":"normal"}'
```

## Inventory and texture API

Look up an inventory item by UUID:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/inventory/items/<item-uuid> `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Look up an inventory item by path:

```powershell
Invoke-RestMethod "http://127.0.0.1:5107/api/inventory/items/by-path?path=Textures/Example Region%20Poster" `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Give an inventory item to an avatar:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/inventory/give `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"avatarId":"<avatar-uuid>","itemId":"<item-uuid>","doEffect":true}'
```

If the item is not already loaded in the bot's inventory cache, include `itemName` and `assetType` so Second Life has the metadata needed for delivery.

Upload a texture asset into the bot's Textures folder:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/textures `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"name":"Example Region Poster","description":"Uploaded by Munibot","textureDataBase64":"<sl-ready-jpeg2000-base64>","confirmUploadFee":true}'
```

Texture upload requires `confirmUploadFee: true` because Second Life may charge the bot account's upload fee depending on account benefits. Munibot echoes the upload capability's expected price during the SL handshake, but wallet/balance events are the source of truth for whether L$ were actually deducted. The first implementation expects SL-ready texture asset bytes, typically JPEG2000, encoded as base64.

## Wallet API

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

Payments require `confirmPayment: true` and the `sl.wallet.pay` token scope. Descriptions are sent to Second Life but are not written to Munibot's structured operation log.

Fetch historical Second Life account transactions for the configured bot web account:

```powershell
Invoke-RestMethod "http://127.0.0.1:5107/api/wallet/account-history?fromUtc=2026-05-22T00:00:00Z&toUtc=2026-05-23T00:00:00Z" `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

This uses the configured bot `login.password` for the Second Life web account login and requires the `sl.wallet.history` token scope. The endpoint is intended for callback reconciliation and returns transaction id, type, resident, timestamp, ending balance, and inferred adjacent-balance deltas.

Munibot can also forward live Second Life money events into a configured callback. Configure the callback URL and shared secret from a configured callback settings:

```yaml
munibase:
  wallet_events:
    endpoint_url: https://example.com/webhooks/second-life-money
    shared_secret: replace-with-callback-secret
    timeout_seconds: 10
    max_delivery_attempts: 3
    retry_delay_seconds: 2
```

The posted payload is Corrade-compatible form data, so the receiving service can use the existing observed-transaction ingestion rules, matching, dedupe, and audit trail while identifying the provider in endpoint logs.

## Avatar resolution API

Resolve avatar UUIDs to names:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/avatars/resolve-keys `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"avatarIds":["11111111-1111-4111-8111-111111111111"]}'
```

Resolve names to candidate UUIDs:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/avatars/resolve-names `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"names":["Example Resident"]}'
```

Search people:

```powershell
Invoke-RestMethod "http://127.0.0.1:5107/api/avatars/search?query=Example" `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

## Group management API

Read group bans:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/bans `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Unban a group member:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/bans/<avatar-uuid>:remove `
  -Method Post `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Invite an avatar to the default Everyone role:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/invites `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"avatarId":"<avatar-uuid>","roleIds":[]}'
```

Eject an avatar:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/members/<avatar-uuid>:eject `
  -Method Post `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Read an avatar's role names:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/groups/<group-uuid>/members/<avatar-uuid>/roles `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

## Estate security API

Estate list operations teleport the bot to an anchor region first so Second Life applies the correct estate context. The bot account must have the appropriate estate manager powers.

Read estate allowed users or banned users:

```powershell
Invoke-RestMethod "http://127.0.0.1:5107/api/estate/allow?anchorRegion=Example Region" `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

```powershell
Invoke-RestMethod "http://127.0.0.1:5107/api/estate/ban?anchorRegion=Example Region" `
  -Headers @{ "X-Munibot-Token" = "<token>" }
```

Add an avatar to the estate allow or ban list:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/estate/ban/<avatar-uuid> `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"anchorRegion":"Example Region","allEstates":false}'
```

Remove an avatar from the estate allow or ban list:

```powershell
Invoke-RestMethod http://127.0.0.1:5107/api/estate/ban/<avatar-uuid>:remove `
  -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Munibot-Token" = "<token>" } `
  -Body '{"anchorRegion":"Example Region","allEstates":false}'
```
