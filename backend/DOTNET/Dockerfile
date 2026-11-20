FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

# Runtime stage uses the Debian-based aspnet image (not Alpine) deliberately —
# Microsoft.AnalysisServices.NetCore.retail.amd64 (added for the TOM/XMLA
# data-model rewrite) ships native libs that are unreliable on musl/Alpine.
# If you switch base images, re-verify the data-model rewrite step still works.
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 5001
ENTRYPOINT ["dotnet", "PbixMigrator.dll"]
