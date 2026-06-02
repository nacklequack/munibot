# Contributing

Munibot is a .NET service backed by LibreMetaverse.

## Local Development

```powershell
dotnet restore .\Munibot.slnx
dotnet test .\Munibot.slnx
dotnet run --project .\src\Munibot\Munibot.csproj -- --config .\config.yaml --urls http://127.0.0.1:5107
```

Use `config.example.yaml` as the starting point for local configuration. Keep `config.yaml` untracked.

## Pull Request Expectations

- Keep API behavior explicit and typed.
- Add or update tests for validators, auth scopes, DTO behavior, redaction, and error mapping when those areas change.
- Avoid logging tokens, passwords, payment descriptions, large payloads, or base64 asset data.
- Prefer small, focused changes over broad refactors.
- Run `dotnet test .\Munibot.slnx` before publishing changes.
