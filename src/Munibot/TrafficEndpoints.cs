namespace Munibot;

public static class TrafficEndpoints
{
    public static void MapTrafficEndpoints(this WebApplication app)
    {
        app.MapGet("/api/regions/{regionName}/parcel-inventory", async (
            string regionName, SecondLifeBotSession session, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await session.GetRegionParcelInventoryAsync(regionName, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (TimeoutException)
            {
                return Results.Problem("Timed out discovering region parcels.", statusCode: StatusCodes.Status504GatewayTimeout);
            }
        }).RequireMunibotScope(AuthScopes.TrafficRead);

        app.MapGet("/api/parcels/{parcelId}/traffic", async (
            string parcelId, SecondLifeBotSession session, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await session.GetParcelTrafficAsync(parcelId, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (TimeoutException)
            {
                return Results.Problem("Timed out waiting for Second Life parcel traffic.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        }).RequireMunibotScope(AuthScopes.TrafficRead);
    }
}

public sealed partial class SecondLifeBotSession
{
    public Task<ParcelTrafficDto> GetParcelTrafficAsync(string parcelId, CancellationToken cancellationToken)
    {
        if (!IsOnline)
            throw new InvalidOperationException("Munibot is not logged in.");

        return new ParcelTrafficReader(new GridParcelInfoSource(_client))
            .ReadAsync(parcelId, TimeSpan.FromSeconds(15), cancellationToken);
    }
}
