FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY src/AtmCash.Domain/AtmCash.Domain.csproj src/AtmCash.Domain/
COPY src/AtmCash.Application/AtmCash.Application.csproj src/AtmCash.Application/
COPY src/AtmCash.Infrastructure/AtmCash.Infrastructure.csproj src/AtmCash.Infrastructure/
COPY src/AtmCash.Api/AtmCash.Api.csproj src/AtmCash.Api/
RUN dotnet restore src/AtmCash.Api/AtmCash.Api.csproj
COPY src/ src/
RUN dotnet publish src/AtmCash.Api/AtmCash.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AtmCash.Api.dll"]
