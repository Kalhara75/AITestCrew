# AITestCrew — Multi-stage Docker build (Windows containers)
#
# The WebApi project targets net8.0-windows (for agent DI registration), so this
# image must run on Windows containers. Switch Docker Desktop to Windows containers
# before building.
#
# PREREQUISITE: Build the React UI on the host first (Windows Server Core containers
# lack the C++ runtime that Vite's native rolldown bundler needs):
#
#   cd ui && npm ci && npx vite build
#
# Then build and run:
#
#   docker compose build
#   docker compose up -d
#
# For Linux hosts or simpler deployments, use publish.ps1 instead.

# ── Stage 1: Restore and publish the .NET application ──
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
# Self-contained publish so the WindowsDesktop runtime (needed by the Agents
# project's WindowsForms reference) is bundled — the aspnet runtime image
# only has Microsoft.NETCore.App + Microsoft.AspNetCore.App.
RUN dotnet publish src/AiTestCrew.WebApi/AiTestCrew.WebApi.csproj -c Release -o C:/app/publish --self-contained -r win-x64

# ── Stage 2: Runtime image ──
FROM mcr.microsoft.com/dotnet/aspnet:8.0-windowsservercore-ltsc2022

# NOTE ON WEB UI TESTS IN THE CONTAINER:
# Windows Server Core containers cannot reliably run headless Chromium.
# They lack the Media Foundation subsystem that Chromium requires, and it
# cannot be installed cleanly:
#   - DISM /enable-feature:ServerMediaFoundation fails with 0x800f081f
#     (Server Core containers don't ship package sources).
#   - Manually copying mf.dll, mfplat.dll, mfreadwrite.dll fails with 0x241
#     (signature verification — DLLs aren't in the container's catalog).
#
# The container is therefore used for: dashboard, SQLite storage, API tests,
# and aseXML generation/delivery. Web UI and Desktop tests should be run
# from the Runner CLI on a host that has the full Windows components.
# See docs/deployment.md — "Test Execution Model".

WORKDIR C:/app
COPY --from=dotnet-build C:/app/publish .

# Pre-built React UI from the host (run `cd ui && npx vite build` first)
COPY ui/dist/ wwwroot/

# Default config — override via environment variables or mounted appsettings.json
ENV ASPNETCORE_URLS=http://+:5050
ENV AITESTCREW_TestEnvironment__ListenUrl=http://+:5050
ENV AITESTCREW_TestEnvironment__StorageProvider=Sqlite
ENV AITESTCREW_TestEnvironment__SqliteConnectionString="Data Source=C:/data/aitestcrew.db"

EXPOSE 5050
ENTRYPOINT ["AiTestCrew.WebApi.exe"]
