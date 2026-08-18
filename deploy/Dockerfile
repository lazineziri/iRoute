FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Hosts/iRoute.Api/iRoute.Api.csproj \
    && dotnet restore src/Hosts/iRoute.Worker/iRoute.Worker.csproj \
    && dotnet restore src/Hosts/iRoute.Migrations/iRoute.Migrations.csproj \
    && dotnet publish src/Hosts/iRoute.Api/iRoute.Api.csproj -c Release --no-restore -o /app/api /p:UseAppHost=false \
    && dotnet publish src/Hosts/iRoute.Worker/iRoute.Worker.csproj -c Release --no-restore -o /app/worker /p:UseAppHost=false \
    && dotnet publish src/Hosts/iRoute.Migrations/iRoute.Migrations.csproj -c Release --no-restore -o /app/migrations /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime-base
ARG VERSION=0.1.0-alpha.3
LABEL org.opencontainers.image.title="iRoute" \
      org.opencontainers.image.description="Task-aware AI execution runtime" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.licenses="Apache-2.0"
RUN addgroup -S iroute \
    && adduser -S iroute -G iroute \
    && mkdir -p /var/lib/iroute \
    && chown -R iroute:iroute /var/lib/iroute
WORKDIR /app
USER iroute
ENV DOTNET_EnableDiagnostics=0
STOPSIGNAL SIGTERM

FROM runtime-base AS worker
COPY --chown=iroute:iroute --from=build /app/worker .
ENTRYPOINT ["dotnet", "iRoute.Worker.dll"]

FROM runtime-base AS migrate
COPY --chown=iroute:iroute --from=build /app/migrations .
ENTRYPOINT ["dotnet", "iRoute.Migrations.dll"]

FROM runtime-base AS api
COPY --chown=iroute:iroute --from=build /app/api .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME ["/var/lib/iroute"]
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "iRoute.Api.dll"]
