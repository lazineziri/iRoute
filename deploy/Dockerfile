FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/iRoute.Api/iRoute.Api.csproj \
    && dotnet restore src/iRoute.Worker/iRoute.Worker.csproj \
    && dotnet publish src/iRoute.Api/iRoute.Api.csproj -c Release --no-restore -o /app/api /p:UseAppHost=false \
    && dotnet publish src/iRoute.Worker/iRoute.Worker.csproj -c Release --no-restore -o /app/worker /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime-base
RUN addgroup -S iroute && adduser -S iroute -G iroute
WORKDIR /app
USER iroute
ENV DOTNET_EnableDiagnostics=0

FROM runtime-base AS worker
COPY --from=build /app/worker .
ENTRYPOINT ["dotnet", "iRoute.Worker.dll"]

FROM runtime-base AS api
COPY --from=build /app/api .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "iRoute.Api.dll"]
