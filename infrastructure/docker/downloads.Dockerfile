# ClubOS: пакеты для Windows (Edge и Agent, self-contained win-x64) — собираются на Linux и раздаются по /downloads/.
# Контекст сборки — корень репозитория. .NET на ПК клуба не нужен: runtime внутри пакета.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY packages/contracts-dotnet/ packages/contracts-dotnet/
COPY packages/security-dotnet/ packages/security-dotnet/
COPY services/edge-controller/ services/edge-controller/
COPY services/edge-cli/ services/edge-cli/
COPY services/windows-agent/ services/windows-agent/
ARG CLUBOS_VERSION=dev
RUN set -e; \
    pub() { dotnet publish "$1" -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -o "$2"; }; \
    pub services/windows-agent/ClubOS.Agent.Service/ClubOS.Agent.Service.csproj /pkg/agent; \
    pub services/windows-agent/ClubOS.Agent.SessionHost/ClubOS.Agent.SessionHost.csproj /pkg/agent; \
    pub services/edge-controller/ClubOS.EdgeController.csproj /pkg/edge; \
    pub services/edge-cli/ClubOS.EdgeCli.csproj /pkg/edge; \
    cp services/windows-agent/install/* /pkg/agent/; \
    cp services/edge-controller/install/* /pkg/edge/; \
    rm -f /pkg/agent/*.pdb /pkg/edge/*.pdb; \
    echo "$CLUBOS_VERSION" > /pkg/agent/VERSION.txt; echo "$CLUBOS_VERSION" > /pkg/edge/VERSION.txt; \
    mkdir -p /downloads; \
    pwsh -NoProfile -Command "Compress-Archive -Path /pkg/agent/* -DestinationPath /downloads/ClubOS-Agent-win-x64.zip; \
                              Compress-Archive -Path /pkg/edge/* -DestinationPath /downloads/ClubOS-Edge-win-x64.zip"; \
    cd /downloads && sha256sum *.zip > SHA256SUMS.txt && echo "$CLUBOS_VERSION" > VERSION.txt

FROM caddy:2
COPY --from=build /downloads /srv
EXPOSE 8080
CMD ["caddy", "file-server", "--root", "/srv", "--listen", ":8080"]
