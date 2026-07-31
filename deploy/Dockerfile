FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/iRoute.Api/iRoute.Api.csproj \
    && dotnet publish src/iRoute.Api/iRoute.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
RUN addgroup -S iroute && adduser -S iroute -G iroute
WORKDIR /app
COPY --from=build /app .
USER iroute
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "iRoute.Api.dll"]
