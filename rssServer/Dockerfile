FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /app

COPY rssServer/*.fsproj ./rssServer/
COPY rssSummary/*.fsproj ./rssSummary/

RUN dotnet restore rssServer/RssScraper.fsproj

COPY rssServer/ ./rssServer/
COPY rssSummary/ ./rssSummary/

RUN dotnet publish rssServer/RssScraper.fsproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

COPY --from=build /app/out ./

ENTRYPOINT ["dotnet", "RssScraper.dll"]
