FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props iRoute.slnx ./
COPY src/iRoute.Common/iRoute.Common.csproj src/iRoute.Common/
COPY src/iRoute.Services/iRoute.Services.csproj src/iRoute.Services/
COPY src/iRoute.Data/iRoute.Data.csproj src/iRoute.Data/
COPY src/iRoute.Core/iRoute.Core.csproj src/iRoute.Core/
COPY src/iRoute.Runtime/iRoute.Runtime.csproj src/iRoute.Runtime/
RUN dotnet restore src/iRoute.Runtime/iRoute.Runtime.csproj
COPY src/iRoute.Common/ src/iRoute.Common/
COPY src/iRoute.Services/ src/iRoute.Services/
COPY src/iRoute.Data/ src/iRoute.Data/
COPY src/iRoute.Core/ src/iRoute.Core/
COPY src/iRoute.Runtime/ src/iRoute.Runtime/
RUN dotnet publish src/iRoute.Runtime/iRoute.Runtime.csproj \
    --configuration Release \
    --no-restore \
    --output /app/iroute \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime-base
ARG VERSION=0.1.0-alpha.4
ARG REVISION=unknown
LABEL org.opencontainers.image.title="iRoute" \
      org.opencontainers.image.description="Task-aware AI execution runtime" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.source="https://github.com/lazineziri/iRoute" \
      org.opencontainers.image.licenses="Apache-2.0"
RUN apk add --no-cache krb5-libs \
    && addgroup -S iroute \
    && adduser -S iroute -G iroute \
    && mkdir -p /var/lib/iroute \
    && chown -R iroute:iroute /var/lib/iroute
WORKDIR /app
USER iroute
ENV DOTNET_EnableDiagnostics=0
STOPSIGNAL SIGTERM

FROM runtime-base AS runtime
COPY --chown=iroute:iroute --from=build /app/iroute .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME ["/var/lib/iroute"]
ENTRYPOINT ["dotnet", "iroute.dll"]
CMD ["serve"]
