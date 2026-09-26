using OpenMetaverse;

namespace Munibot.Tests;

public sealed class ArtifactOfferGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly UUID SourceObject = UUID.Random();
    private static readonly UUID SourceOwner = UUID.Random();
    private static readonly UUID Asset = UUID.Random();
    private static readonly UUID Bot = UUID.Random();
    private static readonly InventoryPermissionMasksDto Masks = new(
        (uint)PermissionMask.All,
        (uint)PermissionMask.All,
        (uint)(PermissionMask.Move | PermissionMask.Modify | PermissionMask.Copy),
        (uint)PermissionMask.Copy,
        (uint)(PermissionMask.Move | PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer));

    [Fact]
    public void ExpectedOfferAdvancesOnlyAfterExactReceipt()
    {
        var gate = Gate(out var expectation);
        var itemId = UUID.Random();

        var decision = gate.HandleOffer(Signal(), Now.AddSeconds(1));

        Assert.True(decision.Accept);
        Assert.Equal("receiving", gate.Get(expectation.RequestId, Now.AddSeconds(1)).Status);
        Assert.True(gate.TryBeginReceipt(itemId, Now.AddSeconds(2)));
        gate.Complete(Item(itemId), Bot, Now.AddSeconds(2));
        var status = gate.Get(expectation.RequestId, Now.AddSeconds(2));
        Assert.Equal("received", status.Status);
        Assert.Equal(itemId.ToString(), status.Receipt!.Item.ItemId);
        Assert.Equal(Asset.ToString(), status.Receipt.Item.AssetId);
        Assert.Equal(Masks, status.Receipt.Item.Permissions);
        Assert.Equal(SourceObject.ToString(), status.Receipt.SourceObjectUuid);
    }

    [Fact]
    public void UnsolicitedAndLateOffersAreRejected()
    {
        var gate = new ArtifactOfferGate(TimeSpan.FromSeconds(5));
        Assert.False(gate.HandleOffer(Signal(), Now).Accept);
        var expectation = Expectation();
        gate.Register(expectation, Now);

        var late = gate.HandleOffer(Signal(), Now.AddSeconds(6));

        Assert.False(late.Accept);
        var status = gate.Get(expectation.RequestId, Now.AddSeconds(6));
        Assert.Equal("expired", status.Status);
        Assert.Equal("offer_expired", status.ErrorCode);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("source-name")]
    [InlineData("inventory-name")]
    [InlineData("type")]
    [InlineData("not-task")]
    public void MismatchedOfferDoesNotConsumeExpectation(string mismatch)
    {
        var gate = Gate(out var expectation);
        var signal = Signal() with
        {
            SourceOwnerId = mismatch == "owner" ? UUID.Random() : SourceOwner,
            SourceObjectName = mismatch == "source-name" ? "Other Drop Box" : "Community Drop Box",
            InventoryName = mismatch == "inventory-name" ? "Other Artifact" : "Scanner HUD",
            AssetType = mismatch == "type" ? AssetType.Notecard : AssetType.Object,
            FromTask = mismatch != "not-task"
        };

        Assert.False(gate.HandleOffer(signal, Now.AddSeconds(1)).Accept);
        Assert.Equal("pending", gate.Get(expectation.RequestId, Now.AddSeconds(1)).Status);
        Assert.True(gate.HandleOffer(Signal(), Now.AddSeconds(2)).Accept);
    }

    [Fact]
    public void DuplicateOfferIsRejectedWhileReceivingAndAfterReceipt()
    {
        var gate = Gate(out _);
        var itemId = UUID.Random();
        Assert.True(gate.HandleOffer(Signal(), Now.AddSeconds(1)).Accept);
        Assert.False(gate.HandleOffer(Signal(), Now.AddSeconds(2)).Accept);
        Assert.True(gate.TryBeginReceipt(itemId, Now.AddSeconds(3)));
        gate.Complete(Item(itemId), Bot, Now.AddSeconds(3));
        Assert.False(gate.HandleOffer(Signal(), Now.AddSeconds(4)).Accept);
    }

    [Fact]
    public void ReceiptTimeoutIsRetryableWithUnknownOutcome()
    {
        var expectation = Expectation();
        var gate = new ArtifactOfferGate(TimeSpan.FromSeconds(5));
        gate.Register(expectation, Now);
        Assert.True(gate.HandleOffer(Signal(), Now.AddSeconds(1)).Accept);

        var status = gate.Get(expectation.RequestId, Now.AddSeconds(6));

        Assert.Equal("failed", status.Status);
        Assert.Equal("receipt_timeout", status.ErrorCode);
        Assert.True(status.Retryable);
        Assert.True(status.OutcomeUnknown);
        var error = Assert.Throws<ArtifactRelayException>(() =>
            gate.RequireReceipt(expectation.RequestId, Now.AddSeconds(6)));
        Assert.Equal("receipt_timeout", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public void ReceiptEventSuppliesTheTaskInventoryItemId()
    {
        var gate = Gate(out _);
        var receivedItemId = UUID.Random();
        var otherItemId = UUID.Random();

        Assert.True(gate.HandleOffer(Signal(), Now.AddSeconds(1)).Accept);
        Assert.False(gate.TryBeginReceipt(UUID.Zero, Now.AddSeconds(2)));
        Assert.True(gate.TryBeginReceipt(receivedItemId, Now.AddSeconds(2)));
        Assert.False(gate.TryBeginReceipt(otherItemId, Now.AddSeconds(2)));
        Assert.True(gate.Complete(Item(receivedItemId), Bot, Now.AddSeconds(3)));
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("permissions")]
    [InlineData("owner")]
    [InlineData("name")]
    public void ReceiptMismatchFailsWithoutProducingReceipt(string mismatch)
    {
        var gate = Gate(out var expectation);
        var itemId = UUID.Random();
        gate.HandleOffer(Signal(), Now.AddSeconds(1));
        gate.TryBeginReceipt(itemId, Now.AddSeconds(2));
        var item = Item(itemId);
        if (mismatch == "asset") item.AssetUUID = UUID.Random();
        if (mismatch == "owner") item.OwnerID = UUID.Random();
        if (mismatch == "name") item.Name = "Other Artifact";
        if (mismatch == "permissions") item.Permissions = new Permissions(0, 0, 0, 0, 0);

        gate.Complete(item, Bot, Now.AddSeconds(2));

        var status = gate.Get(expectation.RequestId, Now.AddSeconds(2));
        Assert.Equal("failed", status.Status);
        Assert.Equal("receipt_mismatch", status.ErrorCode);
        Assert.Null(status.Receipt);
    }

    [Fact]
    public void SameRequestIsIdempotentButChangedContractConflicts()
    {
        var gate = Gate(out var expectation);
        Assert.Equal("pending", gate.Register(expectation, Now.AddSeconds(1)).Status);
        var changed = expectation with { AssetId = UUID.Random() };
        var error = Assert.Throws<ArtifactRelayException>(() => gate.Register(changed, Now.AddSeconds(2)));
        Assert.Equal("request_conflict", error.Code);
    }

    [Theory]
    [InlineData("'Scanner HUD'. (Community Drop Box is located at Briarmont)", "Scanner HUD")]
    [InlineData("'Scanner HUD'", "Scanner HUD")]
    [InlineData("Scanner HUD", "Scanner HUD")]
    [InlineData("'unterminated", null)]
    public void ParsesOnlyTheWireVisibleInventoryName(string message, string? expected) =>
        Assert.Equal(expected, ArtifactOfferProtocol.ReadInventoryName(message));

    private static ArtifactOfferGate Gate(out ArtifactOfferExpectation expectation)
    {
        expectation = Expectation();
        var gate = new ArtifactOfferGate(TimeSpan.FromSeconds(30));
        gate.Register(expectation, Now);
        return gate;
    }

    private static ArtifactOfferExpectation Expectation() => new(
        Guid.NewGuid(), SourceObject, SourceOwner, "Community Drop Box", "Scanner HUD", Asset, Masks);

    private static ArtifactOfferSignal Signal() => new(
        true, SourceOwner, "Community Drop Box", "Scanner HUD", AssetType.Object);

    private static InventoryItem Item(UUID itemId) => new(InventoryType.Object, itemId)
    {
        AssetUUID = Asset,
        AssetType = AssetType.Object,
        InventoryType = InventoryType.Object,
        OwnerID = Bot,
        GroupID = UUID.Zero,
        Name = "Scanner HUD",
        Permissions = new Permissions(Masks.Base, Masks.Everyone, Masks.Group, Masks.NextOwner, Masks.Owner)
    };
}
