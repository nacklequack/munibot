# Security

Munibot controls a live Second Life account and can perform privileged operations when configured with the appropriate token scopes and in-world permissions.

## Deployment Guidance

- Keep Munibot behind private networking whenever possible.
- Configure API tokens for every production deployment.
- Do not expose Munibot directly to the public internet.
- Use long, random token values and rotate them if they are disclosed.
- Never commit `config.yaml`, Second Life credentials, MFA values, API tokens, callback secrets, or wallet/payment test data.
- Treat wallet, inventory, group, estate, and teleport scopes as privileged service capabilities.

## Reporting Issues

If you find a security issue, report it privately to the repository owner. Do not open a public issue containing credentials, exploit details, private endpoint URLs, or live Second Life account information.
