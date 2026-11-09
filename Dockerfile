FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY RiisSaveEditor.csproj ./
RUN dotnet restore RiisSaveEditor.csproj
COPY . .
RUN dotnet publish RiisSaveEditor.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Writable state (signing key, data protection keys); mount a volume here to keep it.
RUN mkdir -p /app/App_Data && chown -R $APP_UID /app/App_Data
VOLUME /app/App_Data
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "RiisSaveEditor.dll"]
