# syntax=docker/dockerfile:1
# Build from the repository root:
#   docker build -f src/Tradeground.Api/Dockerfile -t tradeground-api .

# ---- Stage 1: build with the full SDK (~800 MB, thrown away afterwards) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Copy only project files first and restore. Docker caches this layer, so NuGet packages
# are re-downloaded only when a .csproj or package version changes, not on every code edit.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Tradeground.Engine/Tradeground.Engine.csproj src/Tradeground.Engine/
COPY src/Tradeground.Game/Tradeground.Game.csproj src/Tradeground.Game/
COPY src/Tradeground.Api/Tradeground.Api.csproj src/Tradeground.Api/
RUN dotnet restore src/Tradeground.Api/Tradeground.Api.csproj

COPY src/ src/
RUN dotnet publish src/Tradeground.Api/Tradeground.Api.csproj \
    --configuration Release --no-restore --output /app

# ---- Stage 2: run with just the ASP.NET runtime (~220 MB) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Never run as root inside the container. APP_UID is a non-root user built into the image.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Tradeground.Api.dll"]
