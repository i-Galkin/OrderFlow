FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/OrderFlow.Contracts/OrderFlow.Contracts.csproj src/OrderFlow.Contracts/
COPY src/OrderFlow.Infrastructure/OrderFlow.Infrastructure.csproj src/OrderFlow.Infrastructure/
COPY src/OrderFlow.Worker/OrderFlow.Worker.csproj src/OrderFlow.Worker/
RUN dotnet restore src/OrderFlow.Worker/OrderFlow.Worker.csproj

COPY src/ src/
RUN dotnet publish src/OrderFlow.Worker/OrderFlow.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
# Prometheus scrape endpoint (Observability__MetricsListenerPrefix)
EXPOSE 9464
ENTRYPOINT ["dotnet", "OrderFlow.Worker.dll"]
