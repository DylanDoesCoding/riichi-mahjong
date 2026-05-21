# =============================================================================
# Dockerfile — Riichi Mahjong multiplayer server
# Build context: repo root (so we can reach shared/ and src/)
# =============================================================================

# ---- Build stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first so NuGet restore is cached separately
COPY shared/RiichiMahjong.Core.csproj  shared/
COPY server/RiichiServer.csproj        server/

RUN dotnet restore server/RiichiServer.csproj

# Copy source that the shared library compiles (referenced via Compile globs)
COPY src/Core/ src/Core/
COPY src/AI/   src/AI/

# Copy server source
COPY server/   server/

# Publish optimised release build
RUN dotnet publish server/RiichiServer.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---- Runtime stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Fly.io sets PORT env var; ASP.NET Core reads ASPNETCORE_URLS
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "RiichiServer.dll"]
