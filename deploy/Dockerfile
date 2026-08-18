FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/iRoute.Runtime/iRoute.Runtime.csproj \
    && dotnet publish src/iRoute.Runtime/iRoute.Runtime.csproj -c Release --no-restore -o /app/iroute /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime-base
ARG VERSION=0.1.0-alpha.4
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

FROM runtime-base AS iroute
COPY --chown=iroute:iroute --from=build /app/iroute .

FROM iroute AS worker
ENTRYPOINT ["dotnet", "iroute.dll", "worker"]

FROM iroute AS migrate
ENTRYPOINT ["dotnet", "iroute.dll", "migrate"]

FROM iroute AS api
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME ["/var/lib/iroute"]
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "iroute.dll", "serve"]
