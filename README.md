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
