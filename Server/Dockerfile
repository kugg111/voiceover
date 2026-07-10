FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Server.csproj .
RUN dotnet restore Server.csproj
COPY . .
RUN dotnet publish Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV DATA_DIR=/app/data
ENTRYPOINT ["dotnet", "Server.dll"]
