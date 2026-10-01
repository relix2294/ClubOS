# ClubOS Edge Controller (.NET 10 + SQLite WAL). Контекст сборки — корень репозитория.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY packages/contracts-dotnet/ packages/contracts-dotnet/
COPY packages/security-dotnet/ packages/security-dotnet/
COPY services/edge-controller/ services/edge-controller/
COPY services/edge-cli/ services/edge-cli/
RUN dotnet publish services/edge-controller/ClubOS.EdgeController.csproj -c Release -o /app --no-self-contained \
 && dotnet publish services/edge-cli/ClubOS.EdgeCli.csproj -c Release -o /app/cli --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/edge && chown -R app:app /data \
 && printf '#!/bin/sh\nexec dotnet /app/cli/edge-cli.dll --data /data/edge "$@"\n' > /usr/local/bin/edge-cli \
 && chmod +x /usr/local/bin/edge-cli
USER app
ENV CLUBOS_Edge__DataPath=/data/edge
# 7070 — API агентов (LAN); 7071 — локальный admin API (только loopback внутри контейнера, для edge-cli).
EXPOSE 7070
ENTRYPOINT ["dotnet", "ClubOS.EdgeController.dll"]
