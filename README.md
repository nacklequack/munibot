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
