# syntax=docker/dockerfile:1

# The relay, as the small container app the plan budgets for. The build context is this directory
# rather than the repository root, and that is a guard rather than a convenience: the relay has no
# project reference to MoleSim on purpose, because the moment it can see a Plan type it becomes
# possible for it to start having opinions about one. A context that cannot reach the simulation
# means adding that reference breaks the image build rather than passing quietly.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The project file on its own first, so a change to a source file does not re-download the world.
COPY Relay.Api/Relay.Api.csproj Relay.Api/
RUN dotnet restore Relay.Api/Relay.Api.csproj

COPY Relay.Api/ Relay.Api/
RUN dotnet publish Relay.Api/Relay.Api.csproj --configuration Release --no-restore --output /app

# The runtime image rather than a chiselled or Alpine one. SQLite arrives as a native library
# through SQLitePCLRaw, and the small images are where that stops being somebody else's problem:
# musl needs a different build of it, and finding that out from a container that starts and then
# throws on the first query is a bad afternoon. This image is bigger and boring.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# The database lives on a volume, because the whole point of Anytime pace is that a match outlives
# the process. An in-memory store is right for a local run and wrong for anything a player might
# come back to, so the image is configured for the second case and a local run overrides it.
RUN mkdir -p /data && chown $APP_UID /data

USER $APP_UID
VOLUME /data

ENV Relay__Database=/data/relay.sqlite

# What the .NET images have listened on since .NET 8. Named here so that reading this file is
# enough to know where to point a load balancer.
EXPOSE 8080

# No HEALTHCHECK instruction, deliberately. The image has no curl and no wget, adding one to run a
# health check every thirty seconds is a package and an attack surface for something the platform
# already does better, and every host this would run on probes an HTTP endpoint itself. The endpoint
# is /health and it answers without touching the database.
ENTRYPOINT ["dotnet", "Relay.Api.dll"]
