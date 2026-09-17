using OpenMetaverse;

namespace Munibot;

public static class TextureDeliveryEndpoints
{
    public static void MapTextureDeliveryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/inventory/give-texture", async (FullPermissionTextureRequestDto request,
            SecondLifeBotSession session, CancellationToken ct) =>
        {
            try { return Results.Ok(await session.GiveFullPermissionTextureAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (TimeoutException ex) { return Results.Problem(ex.Message, statusCode: 504); }
        }).RequireMunibotScope(AuthScopes.InventoryGive);
    }
}

public sealed partial class SecondLifeBotSession
{
    public async Task<FullPermissionTextureResultDto> GiveFullPermissionTextureAsync(FullPermissionTextureRequestDto request,
        CancellationToken ct)
    {
        EnsureOnline();
        var avatar = InventoryRequestValidator.NormalizeAvatarId(request.AvatarId);
        if (!UUID.TryParse(request.ItemId, out var item) || item == UUID.Zero
            || !UUID.TryParse(request.AssetId, out var asset) || asset == UUID.Zero)
            throw new ArgumentException("A texture inventory item and minted asset UUID are required.");
        await _inventoryLock.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.Api.InventoryOperationTimeoutSeconds));
            var delivery = new TextureInventoryDelivery(
                (id, owner, token) => _client.Inventory.FetchItemAsync(id, owner, token),
                (id, changes, token) => _client.AisClient.UpdateItemAsync(id, changes, token),
                (id, name, type, recipient) => _client.Inventory.GiveItem(id, name, type, recipient, true));
            await delivery.SendAsync(item, asset, _client.Self.AgentID, avatar, timeout.Token);
            return new(true, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Texture delivery timed out; the recipient's acceptance is unknown."); }
        finally { _inventoryLock.Release(); }
    }
}
