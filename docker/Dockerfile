FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Tests gate the image: a failing test fails the build.
RUN dotnet test --solution Jellyfin.Plugin.TorrCatalog.slnx
RUN dotnet publish src/Jellyfin.Plugin.TorrCatalog -c Release -o /publish

FROM jellyfin/jellyfin:12.1
COPY --from=build /publish/Jellyfin.Plugin.TorrCatalog.dll /opt/torrcatalog/
COPY docker/entrypoint.sh /torrcatalog-entrypoint.sh
ENTRYPOINT ["/torrcatalog-entrypoint.sh"]
