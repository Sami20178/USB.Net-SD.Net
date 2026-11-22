#!/bin/sh
set -eu

version="$(dotnet --version)"
case "$version" in
  8.*) ;;
  *) echo "This project requires the .NET 8 SDK; found $version." >&2; exit 1 ;;
esac

dotnet build src/USB.Net.Core/USB.Net.Core.csproj --configuration Release
dotnet build src/USB.Net/USB.Net.csproj --configuration Release
dotnet build src/SD.Net/SD.Net.csproj --configuration Release
dotnet build src/USB.Net.Android/USB.Net.Android.csproj --configuration Release
dotnet build src/SD.Net.Android/SD.Net.Android.csproj --configuration Release
dotnet run --project tests/USB.Net.Core.SelfTest/USB.Net.Core.SelfTest.csproj --configuration Release
