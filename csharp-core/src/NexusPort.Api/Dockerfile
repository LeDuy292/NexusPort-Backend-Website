FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5000
EXPOSE 5001

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["NexusPort.sln", "./"]
COPY ["src/Shared/NexusPort.Shared.csproj", "src/Shared/"]
COPY ["src/Infrastructure/NexusPort.Infrastructure.csproj", "src/Infrastructure/"]
COPY ["src/NexusPort.Api/NexusPort.Api.csproj", "src/NexusPort.Api/"]
COPY ["src/Modules/Identity/NexusPort.Modules.Identity.csproj", "src/Modules/Identity/"]
COPY ["src/Modules/Booking/NexusPort.Modules.Booking.csproj", "src/Modules/Booking/"]
COPY ["src/Modules/Vessel/NexusPort.Modules.Vessel.csproj", "src/Modules/Vessel/"]
COPY ["src/Modules/Berth/NexusPort.Modules.Berth.csproj", "src/Modules/Berth/"]
COPY ["src/Modules/Container/NexusPort.Modules.Container.csproj", "src/Modules/Container/"]
COPY ["src/Modules/Yard/NexusPort.Modules.Yard.csproj", "src/Modules/Yard/"]
COPY ["src/Modules/Gate/NexusPort.Modules.Gate.csproj", "src/Modules/Gate/"]
COPY ["src/Modules/Dispatcher/NexusPort.Modules.Dispatcher.csproj", "src/Modules/Dispatcher/"]
COPY ["src/Modules/Vehicle/NexusPort.Modules.Vehicle.csproj", "src/Modules/Vehicle/"]
COPY ["src/Modules/Driver/NexusPort.Modules.Driver.csproj", "src/Modules/Driver/"]
COPY ["src/Modules/Equipment/NexusPort.Modules.Equipment.csproj", "src/Modules/Equipment/"]

RUN dotnet restore "src/NexusPort.Api/NexusPort.Api.csproj"
COPY . .
WORKDIR "/src/src/NexusPort.Api"
RUN dotnet build "NexusPort.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "NexusPort.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "NexusPort.Api.dll"]
