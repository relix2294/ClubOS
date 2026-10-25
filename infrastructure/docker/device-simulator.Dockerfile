# ClubOS Device Simulator — SIMULATED ПК для dev/демо. Контекст сборки — корень репозитория.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY packages/contracts-dotnet/ packages/contracts-dotnet/
COPY packages/security-dotnet/ packages/security-dotnet/
COPY services/windows-agent/ClubOS.Agent.Core/ services/windows-agent/ClubOS.Agent.Core/
COPY tools/device-simulator/ tools/device-simulator/
RUN dotnet publish tools/device-simulator/ClubOS.DeviceSimulator.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/sim && chown -R app:app /data
USER app
ENV CLUBOS_SIM_STATE=/data/sim
ENTRYPOINT ["dotnet", "device-simulator.dll"]
