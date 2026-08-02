# syntax=docker/dockerfile:1

FROM node:24.15.0-alpine AS web-build
WORKDIR /src/trading-system-web
COPY trading-system-web/package.json trading-system-web/package-lock.json ./
RUN npm ci
COPY trading-system-web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.10-alpine AS api-build
WORKDIR /src
COPY TradingSystem.slnx Directory.Build.props global.json ./
COPY src/ ./src/
RUN dotnet restore src/TradingSystem.Api/TradingSystem.Api.csproj
RUN dotnet publish src/TradingSystem.Api/TradingSystem.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false
COPY --from=web-build /src/trading-system-web/dist/trading-system-web/browser/ /app/publish/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-alpine AS runtime
WORKDIR /app
RUN addgroup -S trading && adduser -S trading -G trading
COPY --from=api-build --chown=trading:trading /app/publish/ ./
USER trading
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Trading__Mode=Paper
EXPOSE 8080
ENTRYPOINT ["dotnet", "TradingSystem.Api.dll"]
