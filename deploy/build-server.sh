#!/usr/bin/env bash
# Publish the server as a self-contained linux-x64 binary.
# Runs from Windows (Git Bash) or from the target box itself.
set -euo pipefail

cd "$(dirname "$0")/.."

dotnet publish server/FastTranslation.Server/FastTranslation.Server.csproj \
	-c Release \
	-r linux-x64 \
	--self-contained true \
	-o publish/server-linux

echo
echo "published: publish/server-linux"
ls -lh publish/server-linux/FastTranslation.Server
