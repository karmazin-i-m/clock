# Build context is Server/ (see compose.yaml) so this can see the whole solution.
# The cabinet is built in its own Node stage, so the SDK image needs no Node; the publish
# below picks up web/dist and skips running npm itself.
FROM node:24-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=web /web/dist web/dist
RUN dotnet publish src/KClock.Api/KClock.Api.csproj -c Release -o /app -p:SkipCabinetBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "KClock.Api.dll"]
