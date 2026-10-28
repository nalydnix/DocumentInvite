# Build context is the repository root:
#   docker build -f src/DocumentInvite.Api/Dockerfile -t documentinvite-api .
# Behind a TLS-inspecting proxy, also pass its CA bundle:
#   --secret id=cabundle,src=/path/to/ca-bundle.crt
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/DocumentInvite.Api/DocumentInvite.Api.csproj src/DocumentInvite.Api/
RUN --mount=type=secret,id=cabundle,required=false \
    if [ -f /run/secrets/cabundle ]; then export SSL_CERT_FILE=/run/secrets/cabundle; fi; \
    dotnet restore src/DocumentInvite.Api/DocumentInvite.Api.csproj
COPY src/DocumentInvite.Api/ src/DocumentInvite.Api/
RUN dotnet publish src/DocumentInvite.Api/DocumentInvite.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DocumentInvite.Api.dll"]
