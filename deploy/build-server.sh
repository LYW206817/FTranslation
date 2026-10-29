#!/usr/bin/env bash
# 把服务端发布成自包含的 linux-x64 二进制。
# 可以在 Windows（Git Bash）或目标机器上执行。
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
