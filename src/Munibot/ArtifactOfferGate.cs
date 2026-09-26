using OpenMetaverse;

namespace Munibot;

internal sealed class ArtifactOfferGate(TimeSpan? lifetime = null)
{
    private readonly object sync = new();
    private readonly TimeSpan lifetime = lifetime ?? TimeSpan.FromSeconds(60);
    private readonly Dictionary<Guid, Entry> entries = [];
    private Guid? activeRequestId;

    public ArtifactOfferStatusDto Register(ArtifactOfferExpectation expectation, DateTimeOffset now)
    {
        lock (sync)
        {
            ExpireActive(now);
            Prune(now);
            if (entries.TryGetValue(expectation.RequestId, out var existing))
            {
                if (existing.Expectation != expectation)
                    throw new ArtifactRelayException("request_conflict",
                        "The request ID is already bound to a different artifact expectation.");
                return ToDto(existing);
            }
            if (activeRequestId is not null)
                throw new ArtifactRelayException("offer_gate_busy",
                    "Another artifact offer is currently expected.", true);
            var entry = new Entry(expectation, now + lifetime);
            entries.Add(expectation.RequestId, entry);
            activeRequestId = expectation.RequestId;
            return ToDto(entry);
        }
    }

    public ArtifactOfferStatusDto Get(Guid requestId, DateTimeOffset now)
    {
        lock (sync)
        {
            ExpireActive(now);
            return entries.TryGetValue(requestId, out var entry)
                ? ToDto(entry)
                : throw new KeyNotFoundException("Artifact offer request was not found.");
        }
    }

    public ArtifactOfferDecision HandleOffer(ArtifactOfferSignal offer, DateTimeOffset now)
    {
        lock (sync)
        {
            ExpireActive(now);
            if (activeRequestId is null || !entries.TryGetValue(activeRequestId.Value, out var entry))
                return new(false, "unsolicited");
            if (entry.Status != "pending") return new(false, "duplicate");
            var expected = entry.Expectation;
            if (!offer.FromTask || offer.SourceOwnerId != expected.SourceOwnerId ||
                !string.Equals(offer.SourceObjectName, expected.SourceObjectName, StringComparison.Ordinal) ||
                !string.Equals(offer.InventoryName, expected.InventoryName, StringComparison.Ordinal) ||
                offer.AssetType != AssetType.Object)
                return new(false, "mismatch");
            entry.Status = "receiving";
            return new(true, "expected");
        }
    }

    public bool TryBeginReceipt(UUID itemId, DateTimeOffset now)
    {
        lock (sync)
        {
            ExpireActive(now);
            if (itemId == UUID.Zero || activeRequestId is null ||
                !entries.TryGetValue(activeRequestId.Value, out var entry) || entry.Status != "receiving")
                return false;
            if (entry.ReceivedItemId != UUID.Zero && entry.ReceivedItemId != itemId) return false;
            entry.ReceivedItemId = itemId;
            return true;
        }
    }

    public bool Complete(InventoryItem item, UUID botId, DateTimeOffset now)
    {
        lock (sync)
        {
            ExpireActive(now);
            if (activeRequestId is null || !entries.TryGetValue(activeRequestId.Value, out var entry) ||
                entry.Status != "receiving" || entry.ReceivedItemId != item.UUID) return false;
            var expected = entry.Expectation;
            var mismatch = item.AssetType != AssetType.Object || item.InventoryType != InventoryType.Object ||
                item.OwnerID != botId || item.AssetUUID != expected.AssetId || item.Name != expected.InventoryName ||
                !expected.Permissions.Matches(item.Permissions);
            if (mismatch)
            {
                Fail(entry, "receipt_mismatch",
                    "The received inventory item did not match the expected identity and permissions.", false, false);
                return false;
            }
            entry.Status = "received";
            entry.Receipt = new ArtifactOfferReceiptDto(
                expected.RequestId.ToString(), expected.SourceObjectId.ToString(), now,
                ArtifactInventoryItemDto.From(item));
            activeRequestId = null;
            return true;
        }
    }

    public void FailReceipt(UUID itemId, string code, string message, bool retryable, DateTimeOffset now)
    {
        lock (sync)
        {
            ExpireActive(now);
            if (activeRequestId is null || !entries.TryGetValue(activeRequestId.Value, out var entry) ||
                entry.Status != "receiving" || entry.ReceivedItemId != itemId) return;
            Fail(entry, code, message, retryable, true);
        }
    }

    public ArtifactOfferReceiptDto RequireReceipt(Guid requestId, DateTimeOffset now)
    {
        lock (sync)
        {
            ExpireActive(now);
            if (!entries.TryGetValue(requestId, out var entry))
                throw new KeyNotFoundException("Artifact offer request was not found.");
            if (entry.Receipt is not null) return entry.Receipt;
            if (entry.ErrorCode is not null)
                throw new ArtifactRelayException(entry.ErrorCode, entry.Error!, entry.Retryable,
                    entry.OutcomeUnknown);
            throw new ArtifactRelayException("offer_not_received",
                "The expected artifact has not produced a verified receipt.", true,
                entry.Status == "receiving");
        }
    }

    private void ExpireActive(DateTimeOffset now)
    {
        if (activeRequestId is null || !entries.TryGetValue(activeRequestId.Value, out var entry) ||
            now < entry.ExpiresAt) return;
        Fail(entry, entry.Status == "receiving" ? "receipt_timeout" : "offer_expired",
            entry.Status == "receiving"
                ? "The accepted item was not verified before the offer gate expired."
                : "The expected inventory offer expired.", true, entry.Status == "receiving");
    }

    private void Fail(Entry entry, string code, string message, bool retryable, bool outcomeUnknown)
    {
        entry.Status = code == "offer_expired" ? "expired" : "failed";
        entry.ErrorCode = code;
        entry.Error = message;
        entry.Retryable = retryable;
        entry.OutcomeUnknown = outcomeUnknown;
        if (activeRequestId == entry.Expectation.RequestId) activeRequestId = null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var requestId in entries.Where(x => x.Key != activeRequestId &&
                     now - x.Value.ExpiresAt > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToList())
            entries.Remove(requestId);
    }

    private static ArtifactOfferStatusDto ToDto(Entry entry) => new(
        entry.Expectation.RequestId.ToString(), entry.Status, entry.ExpiresAt, entry.Receipt,
        entry.ErrorCode, entry.Error, entry.Retryable, entry.OutcomeUnknown);

    private sealed class Entry(ArtifactOfferExpectation expectation, DateTimeOffset expiresAt)
    {
        public ArtifactOfferExpectation Expectation { get; } = expectation;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string Status { get; set; } = "pending";
        public UUID ReceivedItemId { get; set; }
        public ArtifactOfferReceiptDto? Receipt { get; set; }
        public string? ErrorCode { get; set; }
        public string? Error { get; set; }
        public bool Retryable { get; set; }
        public bool OutcomeUnknown { get; set; }
    }
}
