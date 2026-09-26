using OpenMetaverse;

namespace Munibot;

public sealed partial class SecondLifeBotSession : IArtifactRelayService
{
    private readonly ArtifactOfferGate _artifactOfferGate = new();

    public ArtifactOfferStatusDto RegisterArtifactOffer(string requestId,
        ArtifactOfferExpectationRequestDto request)
    {
        if (!IsOnline)
            throw new ArtifactRelayException("bot_offline", "Munibot is not logged in.", true);
        return _artifactOfferGate.Register(ArtifactRelayValidator.NormalizeExpectation(requestId, request),
            DateTimeOffset.UtcNow);
    }

    public ArtifactOfferStatusDto GetArtifactOffer(string requestId)
    {
        if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Request ID must be a nonzero UUID.");
        return _artifactOfferGate.Get(parsed, DateTimeOffset.UtcNow);
    }

    public async Task<ArtifactRelayResultDto> RelayArtifactAsync(string objectUuid, string requestId,
        ArtifactRelayRequestDto request, CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Request ID must be a nonzero UUID.");
        var spec = ArtifactRelayValidator.NormalizeRelay(objectUuid, request);
        var receipt = _artifactOfferGate.RequireReceipt(parsed, DateTimeOffset.UtcNow);
        try
        {
            return await InTaskInventoryAsync(objectUuid, request.Region, request.Position, async (target, token) =>
            {
                var sourceId = UUID.Parse(receipt.Item.ItemId);
                var source = await _client.Inventory.FetchItemAsync(sourceId, _client.Self.AgentID, token)
                    ?? throw new ArtifactRelayException("source_missing",
                        "The exact received bot inventory item was not found.", true);
                if (!MatchesReceipt(source, receipt.Item))
                    throw new ArtifactRelayException("source_changed",
                        "The bot inventory item no longer matches its verified receipt.");
                var result = await ((GridTaskInventoryTarget)target).RelayArtifactAsync(source, spec, token);
                return new ArtifactRelayResultDto(parsed.ToString(), spec.TargetObjectId.ToString(), spec.ExpectedBundleKey,
                    result.Copied, result.Idempotent, ArtifactInventoryItemDto.From(source),
                    ArtifactInventoryItemDto.From(result.Target));
            }, ct, suppressSensitiveDiagnostics: true);
        }
        catch (TaskInventoryException ex)
        {
            throw new ArtifactRelayException(ex.Code, ex.Message, ex.Retryable, ex.OutcomeUnknown);
        }
    }

    private void HandleArtifactOffer(InventoryObjectOfferedEventArgs offer)
    {
        var signal = new ArtifactOfferSignal(
            offer.FromTask,
            offer.Offer.FromAgentID,
            offer.Offer.FromAgentName ?? string.Empty,
            ArtifactOfferProtocol.ReadInventoryName(offer.Offer.Message) ?? string.Empty,
            offer.AssetType);
        var decision = _artifactOfferGate.HandleOffer(signal, DateTimeOffset.UtcNow);
        offer.Accept = decision.Accept;
        logger.LogInformation("Artifact task offer decision={Decision}", decision.Reason);
    }

    private async Task HandleArtifactItemReceivedAsync(TaskItemReceivedEventArgs received)
    {
        if (!_artifactOfferGate.TryBeginReceipt(received.ItemID, DateTimeOffset.UtcNow)) return;
        logger.LogInformation("Artifact task receipt observed; verifying accepted item");
        try
        {
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(config.Api.InventoryOperationTimeoutSeconds));
            await _inventoryLock.WaitAsync(deadline.Token);
            try
            {
                var item = await _client.Inventory.FetchItemAsync(received.ItemID, _client.Self.AgentID,
                    deadline.Token) ?? throw new ArtifactRelayException("receipt_unconfirmed",
                        "The accepted inventory item could not be fetched.", true, true);
                if (_artifactOfferGate.Complete(item, _client.Self.AgentID, DateTimeOffset.UtcNow))
                    logger.LogInformation("Artifact task receipt verified");
                else
                    logger.LogWarning("Artifact task receipt did not match the registered expectation");
            }
            finally { _inventoryLock.Release(); }
        }
        catch (Exception ex)
        {
            _artifactOfferGate.FailReceipt(received.ItemID, "receipt_unconfirmed",
                "The accepted inventory item could not be verified.", true, DateTimeOffset.UtcNow);
            logger.LogWarning("Accepted artifact receipt verification failed; errorType={ErrorType}",
                ex.GetType().Name);
        }
    }

    private static bool MatchesReceipt(InventoryItem item, ArtifactInventoryItemDto receipt) =>
        item.UUID.ToString() == receipt.ItemId && item.AssetUUID.ToString() == receipt.AssetId &&
        item.Name == receipt.Name && item.AssetType.ToString() == receipt.AssetType &&
        item.InventoryType.ToString() == receipt.InventoryType && item.OwnerID.ToString() == receipt.OwnerId &&
        item.GroupID.ToString() == receipt.GroupId && item.GroupOwned == receipt.GroupOwned &&
        receipt.Permissions.Matches(item.Permissions);
}

internal static class ArtifactOfferProtocol
{
    public static string? ReadInventoryName(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        if (message[0] != '\'') return message;
        var end = message.IndexOf('\'', 1);
        return end > 1 ? message[1..end] : null;
    }
}

internal sealed record ArtifactTargetRelayResult(bool Copied, bool Idempotent, InventoryItem Target);
