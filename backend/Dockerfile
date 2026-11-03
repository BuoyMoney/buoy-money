# dotnet/aspnet:10.0, pinned by digest
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# dotnet/sdk:10.0, pinned by digest
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY ["src/BuoyAI.API/BuoyAI.API.csproj", "src/BuoyAI.API/"]
COPY ["src/BuoyAI.Core/BuoyAI.Core.csproj", "src/BuoyAI.Core/"]
COPY ["src/BuoyAI.Infrastructure/BuoyAI.Infrastructure.csproj", "src/BuoyAI.Infrastructure/"]
RUN dotnet restore "src/BuoyAI.API/BuoyAI.API.csproj"
COPY . .
WORKDIR "/src/src/BuoyAI.API"
RUN dotnet build "BuoyAI.API.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "BuoyAI.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BuoyAI.API.dll"]
