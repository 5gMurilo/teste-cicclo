FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/Cicclo.Api.csproj src/packages.lock.json ./src/
RUN dotnet restore src/Cicclo.Api.csproj --locked-mode
COPY src/ ./src/
RUN dotnet publish src/Cicclo.Api.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Cicclo.Api.dll"]
