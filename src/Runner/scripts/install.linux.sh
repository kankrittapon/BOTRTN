#!/usr/bin/env bash
set -euo pipefail

export DOTNET_ENVIRONMENT=Development
dotnet run --project ./src/Runner/Runner.csproj