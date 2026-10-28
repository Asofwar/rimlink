# Build context: repository root. docker build -f deploy/Dockerfile -t rimlink-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Rimlink.sln Directory.Build.props Directory.Packages.props global.json ./
COPY src/Rimlink.Protocol/Rimlink.Protocol.csproj src/Rimlink.Protocol/
COPY src/Rimlink.Server/Rimlink.Server.csproj src/Rimlink.Server/
RUN dotnet restore src/Rimlink.Server/Rimlink.Server.csproj -r linux-x64
COPY src/ src/
RUN dotnet publish src/Rimlink.Server/Rimlink.Server.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is only for the HEALTHCHECK below.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# All runtime state lives under /data (database, saves, tls, logs, backups).
ENV Rimlink__Server__DataDir=/data \
    Rimlink__Server__Port=25560
RUN mkdir -p /data && chown app:app /data
USER app
VOLUME /data
EXPOSE 25560/tcp
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl -fsk https://127.0.0.1:25560/health || exit 1
ENTRYPOINT ["dotnet", "rimlink-server.dll"]
