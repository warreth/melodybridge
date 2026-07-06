FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy entire repository and restore/build
COPY . ./
RUN dotnet restore

# publish server
WORKDIR /src/MelodyBridge.Server
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 80
ENTRYPOINT ["dotnet", "MelodyBridge.Server.dll"]
