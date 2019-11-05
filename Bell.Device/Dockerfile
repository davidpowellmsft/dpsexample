FROM mcr.microsoft.com/dotnet/core/runtime:3.0-buster-slim AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/core/sdk:3.0-buster AS build
WORKDIR /src
COPY ["Bell.Device/Bell.Device.csproj", "Bell.Device/"]
RUN dotnet restore "Bell.Device/Bell.Device.csproj"
COPY . .
WORKDIR "/src/Bell.Device"
RUN dotnet build "Bell.Device.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Bell.Device.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bell.Device.dll"]