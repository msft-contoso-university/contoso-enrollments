FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY src/Contoso.Enrollments.Api/Contoso.Enrollments.Api.csproj src/Contoso.Enrollments.Api/packages.lock.json src/Contoso.Enrollments.Api/
RUN dotnet restore src/Contoso.Enrollments.Api/Contoso.Enrollments.Api.csproj --locked-mode
COPY src/Contoso.Enrollments.Api/ src/Contoso.Enrollments.Api/
RUN dotnet publish src/Contoso.Enrollments.Api/Contoso.Enrollments.Api.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Contoso.Enrollments.Api.dll"]
