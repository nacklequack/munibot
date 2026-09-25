namespace Munibot;

public interface IArtifactRelayService
{
    ArtifactOfferStatusDto RegisterArtifactOffer(string requestId, ArtifactOfferExpectationRequestDto request);
    ArtifactOfferStatusDto GetArtifactOffer(string requestId);
    Task<ArtifactRelayResultDto> RelayArtifactAsync(string objectUuid, string requestId,
        ArtifactRelayRequestDto request, CancellationToken ct);
}

public static class ArtifactRelayEndpoints
{
    public static void MapArtifactRelayEndpoints(this WebApplication app)
    {
        app.MapPut("/api/inventory/artifact-offers/{requestId}",
            (string requestId, ArtifactOfferExpectationRequestDto request, IArtifactRelayService service) =>
                Execute(() => service.RegisterArtifactOffer(requestId, request)))
            .RequireMunibotScope(AuthScopes.ArtifactRelay);

        app.MapGet("/api/inventory/artifact-offers/{requestId}",
            (string requestId, IArtifactRelayService service) =>
                Execute(() => service.GetArtifactOffer(requestId)))
            .RequireMunibotScope(AuthScopes.ArtifactRelay);

        app.MapPut("/api/objects/{objectUuid}/inventory/artifacts/{requestId}", async (
            string objectUuid, string requestId, ArtifactRelayRequestDto request,
            IArtifactRelayService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.RelayArtifactAsync(objectUuid, requestId, request, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArtifactRelayException ex) { return ErrorResult(ex); }
        }).RequireMunibotScope(AuthScopes.ArtifactRelay);
    }

    private static IResult Execute<T>(Func<T> action)
    {
        try { return Results.Ok(action()); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (ArtifactRelayException ex) { return ErrorResult(ex); }
    }

    public static IResult ErrorResult(ArtifactRelayException error) => Results.Json(
        new ArtifactRelayErrorDto(error.Code, error.Message, error.Retryable, error.OutcomeUnknown),
        statusCode: error.Retryable ? StatusCodes.Status503ServiceUnavailable :
            StatusCodes.Status422UnprocessableEntity);

    public static bool IsSensitivePath(PathString path) =>
        path.StartsWithSegments("/api/inventory/artifact-offers", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/api/objects", StringComparison.OrdinalIgnoreCase) &&
        path.Value?.Contains("/inventory/artifacts/", StringComparison.OrdinalIgnoreCase) == true;
}
