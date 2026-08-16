FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Tard/Tard.csproj", "src/Tard/"]
RUN dotnet restore "src/Tard/Tard.csproj"
COPY . .
WORKDIR "/src/src/Tard"
RUN dotnet build "Tard.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Tard.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# The memory/chat store is written at runtime, so it must be owned by the non-root app user.
RUN mkdir -p /data/memory && chown -R $APP_UID /data/memory
VOLUME /data/memory

EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

# Drop root: this process runs model-directed tool calls.
USER $APP_UID
ENTRYPOINT ["dotnet", "Tard.dll"]
