FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Munibot.csproj ./
RUN dotnet restore Munibot.csproj

COPY . ./
RUN dotnet publish Munibot.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN useradd --create-home --uid 10001 munibot

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://0.0.0.0:5107
EXPOSE 5107

USER munibot

ENTRYPOINT ["dotnet", "Munibot.dll"]
CMD ["--config", "/app/config/config.yaml"]
