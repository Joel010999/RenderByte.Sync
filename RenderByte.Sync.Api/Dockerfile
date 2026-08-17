# Etapa de compilación
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar archivos de proyectos y restaurar
COPY ["RenderByte.Sync.Api/RenderByte.Sync.Api.csproj", "RenderByte.Sync.Api/"]
COPY ["RenderByte.Sync.Contracts/RenderByte.Sync.Contracts.csproj", "RenderByte.Sync.Contracts/"]
RUN dotnet restore "RenderByte.Sync.Api/RenderByte.Sync.Api.csproj"

# Copiar el resto del código y compilar
COPY . .
WORKDIR "/src/RenderByte.Sync.Api"
RUN dotnet build "RenderByte.Sync.Api.csproj" -c Release -o /app/build
RUN dotnet publish "RenderByte.Sync.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Etapa final (runtime)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Railway provee el puerto mediante la variable de entorno PORT.
# ASP.NET Core usa ASPNETCORE_HTTP_PORTS o ASPNETCORE_URLS.
# Ajustamos ASPNETCORE_URLS para escuchar en el puerto proporcionado por Railway.
ENV ASPNETCORE_URLS=http://*:${PORT:-8080}

EXPOSE 8080

ENTRYPOINT ["dotnet", "RenderByte.Sync.Api.dll"]
