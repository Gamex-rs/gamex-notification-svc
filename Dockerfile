# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first so the layer is cached until a project or package version changes.
# .editorconfig carries analyzer settings; without it warnings-as-errors fails the publish.
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY src/Gamex.Notifications.Domain/*.csproj src/Gamex.Notifications.Domain/
COPY src/Gamex.Notifications.Application/*.csproj src/Gamex.Notifications.Application/
COPY src/Gamex.Notifications.Infrastructure/*.csproj src/Gamex.Notifications.Infrastructure/
COPY src/Gamex.Notifications.Api/*.csproj src/Gamex.Notifications.Api/
RUN dotnet restore src/Gamex.Notifications.Api/Gamex.Notifications.Api.csproj

COPY src/ src/
RUN dotnet publish src/Gamex.Notifications.Api/Gamex.Notifications.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gamex.Notifications.Api.dll"]
