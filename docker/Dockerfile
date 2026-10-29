# syntax=docker/dockerfile:1
# Dev images of the server services for docker-compose.yml (spec 11.3: the dev environment). Not for staging or
# production: they are Debug builds, because the dev login and the dev admin sign-in exist only there (DevLogin.props).
# One build stage publishes every service, so `docker compose build` restores and compiles once.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN --mount=type=cache,id=mmorpg-nuget,target=/root/.nuget/packages \
    dotnet publish src/GameServer -c Debug -o /out/gameserver \
 && dotnet publish src/AuthApi -c Debug -o /out/authapi \
 && dotnet publish src/AdminPanel -c Debug -o /out/adminpanel \
 && dotnet publish src/Tools/DbMigrate -c Debug -o /out/dbmigrate

# Common runtime: the ASP.NET Core image (it also runs the console services), the dev scripts, and the folders the
# named volumes are mounted on, owned by the image's non-root user so a fresh volume is writable.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY docker/dev-entrypoint.sh /usr/local/bin/dev-entrypoint
COPY docker/init-dev-secrets.sh /usr/local/bin/init-dev-secrets
RUN sed -i 's/\r$//' /usr/local/bin/dev-entrypoint /usr/local/bin/init-dev-secrets \
 && chmod +x /usr/local/bin/dev-entrypoint /usr/local/bin/init-dev-secrets \
 && mkdir -p /run/dev-secrets /telemetry /logs \
 && chown app:app /run/dev-secrets /telemetry /logs
USER app
WORKDIR /app

FROM runtime AS gameserver
COPY --from=build /out/gameserver .
ENTRYPOINT ["dev-entrypoint", "gameserver", "GameServer.dll"]

FROM runtime AS authapi
COPY --from=build /out/authapi .
ENTRYPOINT ["dev-entrypoint", "authapi", "AuthApi.dll"]

FROM runtime AS adminpanel
COPY --from=build /out/adminpanel .
ENTRYPOINT ["dev-entrypoint", "-", "AdminPanel.dll"]

FROM runtime AS dbmigrate
COPY --from=build /out/dbmigrate .
ENTRYPOINT ["dev-entrypoint", "-", "DbMigrate.dll"]
CMD ["--create-databases"]
