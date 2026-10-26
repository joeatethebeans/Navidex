FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Navidex.sln ./
COPY src/Navidex.Contracts/Navidex.Contracts.csproj src/Navidex.Contracts/
COPY src/Navidex.Client/Navidex.Client.csproj src/Navidex.Client/
COPY src/Navidex.Web/Navidex.Web.csproj src/Navidex.Web/
COPY src/Navidex.Plugins.MusicBrainz/Navidex.Plugins.MusicBrainz.csproj src/Navidex.Plugins.MusicBrainz/
COPY src/Navidex.Plugins.Navidrome/Navidex.Plugins.Navidrome.csproj src/Navidex.Plugins.Navidrome/
COPY src/Navidex.Plugins.FanartTv/Navidex.Plugins.FanartTv.csproj src/Navidex.Plugins.FanartTv/
COPY src/Navidex.Plugins.Wikimedia/Navidex.Plugins.Wikimedia.csproj src/Navidex.Plugins.Wikimedia/
COPY src/Navidex.Plugins.Deezer/Navidex.Plugins.Deezer.csproj src/Navidex.Plugins.Deezer/
COPY src/Navidex.Core/Navidex.Core.csproj src/Navidex.Core/
RUN dotnet restore src/Navidex.Core/Navidex.Core.csproj
COPY src/ src/
RUN dotnet publish src/Navidex.Core/Navidex.Core.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# Staging is shared with the beets worker and the plugins, which run as other users. Whichever image first mounts a
# new volume sets its owner, so every image makes it writable by all, sticky so nobody removes another's jobs.
RUN mkdir -p /config /music /staging /listen-now /plugins \
    && chown -R $APP_UID /config /listen-now /plugins \
    && chmod 1777 /staging
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Navidex__DataPath=/config \
    Navidex__LibraryPath=/music \
    Navidex__StagingPath=/staging \
    Navidex__ListenNowPath=/listen-now \
    Navidex__PluginsPath=/plugins
EXPOSE 8080
VOLUME ["/config", "/staging", "/listen-now"]
USER $APP_UID
ENTRYPOINT ["dotnet", "Navidex.Core.dll"]
