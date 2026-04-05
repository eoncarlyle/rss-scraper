FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /app

COPY rssSummary/*.fsproj ./rssSummary/

RUN dotnet restore rssSummary/RssSummary.fsproj

COPY rssSummary/ ./rssSummary/

RUN dotnet publish rssSummary/RssSummary.fsproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

COPY --from=build /app/out ./

ENTRYPOINT ["dotnet", "RssSummary.dll"]
