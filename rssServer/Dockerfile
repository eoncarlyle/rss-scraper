FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /app

COPY server/*.fsproj ./server/
COPY rssSummary/*.fsproj ./rssSummary/

RUN dotnet restore server/RssScraper.fsproj

COPY server/ ./server/
COPY rssSummary/ ./rssSummary/

RUN dotnet publish server/RssScraper.fsproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

COPY --from=build /app/out ./

ENTRYPOINT ["dotnet", "RssScraper.dll"]
