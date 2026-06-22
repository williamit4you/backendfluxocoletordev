FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NuGet.Config Directory.Build.props FlowTrack.sln ./
COPY src/FlowTrack.Domain/FlowTrack.Domain.csproj src/FlowTrack.Domain/
COPY src/FlowTrack.Application/FlowTrack.Application.csproj src/FlowTrack.Application/
COPY src/FlowTrack.Data/FlowTrack.Data.csproj src/FlowTrack.Data/
COPY src/FlowTrack.IoC/FlowTrack.IoC.csproj src/FlowTrack.IoC/
COPY src/FlowTrack.API/FlowTrack.API.csproj src/FlowTrack.API/
RUN dotnet restore FlowTrack.sln --configfile NuGet.Config

COPY src ./src
RUN dotnet publish src/FlowTrack.API/FlowTrack.API.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FlowTrack.API.dll"]
