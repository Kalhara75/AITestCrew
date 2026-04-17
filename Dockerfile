# AITestCrew — Multi-stage Docker build (Windows containers)
#
# The WebApi project targets net8.0-windows (for agent DI registration), so this
# image must run on Windows containers. Switch Docker Desktop to Windows containers
# before building:
#
#   docker build -t aitestcrew .
#   docker compose up -d
#
# For Linux hosts or simpler deployments, use publish.ps1 instead.

# ── Stage 1: Build the React frontend ──
FROM mcr.microsoft.com/dotnet/sdk:8.0-windowsservercore-ltsc2022 AS ui-build
SHELL ["powershell", "-Command", "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue';"]

# Install Node.js (LTS) into the SDK image
RUN Invoke-WebRequest -Uri 'https://nodejs.org/dist/v20.18.0/node-v20.18.0-win-x64.zip' -OutFile C:\node.zip ; \
    Expand-Archive C:\node.zip -DestinationPath C:\ ; \
    Rename-Item 'C:\node-v20.18.0-win-x64' 'C:\nodejs' ; \
    Remove-Item C:\node.zip
RUN setx /M PATH "C:\nodejs;$env:PATH"

WORKDIR C:/build/ui
COPY ui/package*.json ./
RUN C:\nodejs\npm.cmd ci --ignore-scripts
COPY ui/ .
RUN C:\nodejs\npx.cmd vite build

# ── Stage 2: Restore and publish the .NET application ──
FROM mcr.microsoft.com/dotnet/sdk:8.0-windowsservercore-ltsc2022 AS dotnet-build
WORKDIR C:/src

# Restore (layer-cached when csproj files don't change)
COPY AITestCrew.slnx .
COPY src/AiTestCrew.Core/AiTestCrew.Core.csproj                 src/AiTestCrew.Core/
COPY src/AiTestCrew.Storage/AiTestCrew.Storage.csproj            src/AiTestCrew.Storage/
COPY src/AiTestCrew.Agents/AiTestCrew.Agents.csproj              src/AiTestCrew.Agents/
COPY src/AiTestCrew.Orchestrator/AiTestCrew.Orchestrator.csproj  src/AiTestCrew.Orchestrator/
COPY src/AiTestCrew.WebApi/AiTestCrew.WebApi.csproj              src/AiTestCrew.WebApi/
COPY src/AiTestCrew.Runner/AiTestCrew.Runner.csproj              src/AiTestCrew.Runner/
RUN dotnet restore src/AiTestCrew.WebApi/AiTestCrew.WebApi.csproj

# Build and publish
COPY src/ src/
COPY templates/ templates/
RUN dotnet publish src/AiTestCrew.WebApi/AiTestCrew.WebApi.csproj -c Release -o C:/app/publish --no-restore

# ── Stage 3: Runtime image ──
FROM mcr.microsoft.com/dotnet/aspnet:8.0-windowsservercore-ltsc2022

WORKDIR C:/app
COPY --from=dotnet-build C:/app/publish .
COPY --from=ui-build C:/build/ui/dist wwwroot/

# Default config — override via environment variables or mounted appsettings.json
ENV ASPNETCORE_URLS=http://+:5050
ENV AITESTCREW_TestEnvironment__ListenUrl=http://+:5050
ENV AITESTCREW_TestEnvironment__StorageProvider=Sqlite
ENV AITESTCREW_TestEnvironment__SqliteConnectionString="Data Source=C:/data/aitestcrew.db"

EXPOSE 5050
ENTRYPOINT ["dotnet", "AiTestCrew.WebApi.dll"]
