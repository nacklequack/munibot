namespace Munibot;

internal enum SessionClaimPhase
{
    Released,
    Owned,
    Uncertain,
}

internal enum ProviderSessionCloseOutcome
{
    Acknowledged,
    RegionShutdown,
    Ambiguous,
}

internal sealed record SessionClaimToken(string HolderIdentity, long Generation);

internal sealed record SessionClaimAcquisition(SessionClaimToken? Token, SessionClaimPhase ExistingPhase);

internal sealed record SessionClaimState(
    string ResourceVersion,
    SessionClaimPhase Phase,
    string? HolderIdentity,
    long Generation)
{
    public static SessionClaimState Released(string resourceVersion, long generation = 0)
        => new(resourceVersion, SessionClaimPhase.Released, null, generation);
}

internal interface ISessionClaimStore
{
    Task<SessionClaimState> ReadAsync(CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(
        SessionClaimState expected,
        SessionClaimState replacement,
        CancellationToken cancellationToken);
}

internal interface ISessionClaimCoordinator
{
    Task<SessionClaimAcquisition> AcquireAsync(string processNonce, CancellationToken cancellationToken);
    Task<bool> CloseAsync(
        SessionClaimToken token,
        ProviderSessionCloseOutcome outcome,
        CancellationToken cancellationToken);
}

internal sealed class SessionClaimCoordinator(ISessionClaimStore store) : ISessionClaimCoordinator
{
    public async Task<SessionClaimAcquisition> AcquireAsync(
        string processNonce,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processNonce);

        while (true)
        {
            var current = await store.ReadAsync(cancellationToken);
            if (current.Phase is not SessionClaimPhase.Released ||
                !string.IsNullOrEmpty(current.HolderIdentity))
            {
                return new SessionClaimAcquisition(null, current.Phase);
            }

            var replacement = current with
            {
                Phase = SessionClaimPhase.Owned,
                HolderIdentity = processNonce,
                Generation = checked(current.Generation + 1),
            };

            if (await store.TryReplaceAsync(current, replacement, cancellationToken))
            {
                return new SessionClaimAcquisition(
                    new SessionClaimToken(processNonce, replacement.Generation),
                    replacement.Phase);
            }
        }
    }

    public async Task<bool> CloseAsync(
        SessionClaimToken token,
        ProviderSessionCloseOutcome outcome,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var current = await store.ReadAsync(cancellationToken);
            if (current.Phase is not SessionClaimPhase.Owned ||
                current.Generation != token.Generation ||
                !string.Equals(current.HolderIdentity, token.HolderIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            var replacement = outcome switch
            {
                ProviderSessionCloseOutcome.Acknowledged or ProviderSessionCloseOutcome.RegionShutdown => current with
                {
                    Phase = SessionClaimPhase.Released,
                    HolderIdentity = null,
                },
                _ => current with { Phase = SessionClaimPhase.Uncertain },
            };

            if (await store.TryReplaceAsync(current, replacement, cancellationToken))
            {
                return true;
            }
        }
    }
}
