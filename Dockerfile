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

# Render (and other platforms) inject PORT at runtime; fall back to 8080 locally.
# Use shell form so the variable is expanded at container start, not build time.
EXPOSE 8080
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet RiichiServer.dll"]
