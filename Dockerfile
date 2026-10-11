# One image: the React app is built and served by the API from wwwroot.

FROM node:20-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ClinicReport.Api/ClinicReport.Api.csproj src/ClinicReport.Api/
RUN dotnet restore src/ClinicReport.Api/ClinicReport.Api.csproj
COPY src/ src/
COPY templates/ templates/
RUN dotnet publish src/ClinicReport.Api/ClinicReport.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /web/dist ./wwwroot
RUN mkdir /data && chown app:app /data
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__ClinicReport="Data Source=/data/clinicreport.db" \
    ClinicHub__DatabasePattern="Data Source=/data/clinicreport-{clinic}.db" \
    DataProtection__KeysDirectory=/data/keys
VOLUME /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClinicReport.Api.dll"]
