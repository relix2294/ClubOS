# ClubOS Cloud API (ASP.NET Core, .NET 10). Контекст сборки — корень репозитория.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY packages/contracts-dotnet/ packages/contracts-dotnet/
COPY packages/security-dotnet/ packages/security-dotnet/
COPY services/cloud-api/ services/cloud-api/
RUN dotnet publish services/cloud-api/ClubOS.CloudApi.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Каталог dev CA (том); приватный ключ CA не попадает в образ.
RUN mkdir -p /data/dev-ca && chown -R app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    CLUBOS_DevCa__Path=/data/dev-ca
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClubOS.CloudApi.dll"]
