# Multi-stage build for CmsEvents.Api
# Build with: docker build -f src/CmsEvents.Api/Dockerfile -t cmsevents-api:latest .
# Run with:   docker run -p 8080:8080 cmsevents-api:latest

# ============================================================
# Build stage
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source

# Copy solution-level files for layer caching
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY CmsEvents.sln ./

# Copy csproj files (structure matches solution layout for restore caching)
COPY src/CmsEvents.Api/CmsEvents.Api.csproj src/CmsEvents.Api/
COPY src/CmsEvents.Application/CmsEvents.Application.csproj src/CmsEvents.Application/
COPY src/CmsEvents.Domain/CmsEvents.Domain.csproj src/CmsEvents.Domain/
COPY src/CmsEvents.Infrastructure/CmsEvents.Infrastructure.csproj src/CmsEvents.Infrastructure/
COPY src/CmsEvents.Contracts/CmsEvents.Contracts.csproj src/CmsEvents.Contracts/

# Restore only what Api needs (transitively pulls Application, Infrastructure, Contracts, Domain)
RUN dotnet restore src/CmsEvents.Api/CmsEvents.Api.csproj

# Copy the rest of the source
COPY src/ src/

# Publish
RUN dotnet publish src/CmsEvents.Api/CmsEvents.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ============================================================
# Runtime stage
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Non-root user
RUN groupadd -r cmsevents && useradd -r -g cmsevents cmsevents
USER cmsevents

COPY --from=build --chown=cmsevents:cmsevents /app/publish .

# Configuration expectations:
#   ASPNETCORE_ENVIRONMENT=Production
#   KeyVaultUri=<https://kv-cmsevents-prod.vault.azure.net/>
#   (Managed Identity provides auth; all other secrets from Key Vault)
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CmsEvents.Api.dll"]
