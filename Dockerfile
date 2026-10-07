# Production image: the API plus the administrative CLI (migrations, setup, users).
# Built from the repo root:  docker build -t pairrot-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig ./
COPY src/Pairrot.Core/Pairrot.Core.csproj src/Pairrot.Core/
COPY src/Pairrot.Infrastructure/Pairrot.Infrastructure.csproj src/Pairrot.Infrastructure/
COPY src/Pairrot.Api/Pairrot.Api.csproj src/Pairrot.Api/
COPY src/Pairrot.Admin/Pairrot.Admin.csproj src/Pairrot.Admin/
RUN dotnet restore src/Pairrot.Api/Pairrot.Api.csproj \
 && dotnet restore src/Pairrot.Admin/Pairrot.Admin.csproj
COPY src/ src/
RUN dotnet publish src/Pairrot.Api/Pairrot.Api.csproj -c Release -o /out/api --no-restore \
 && dotnet publish src/Pairrot.Admin/Pairrot.Admin.csproj -c Release -o /out/admin --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/api ./
COPY --from=build /out/admin ./admin/
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Pairrot.Api.dll"]
