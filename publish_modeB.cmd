@echo off
echo === B 模式: 自包含单文件 ===
dotnet publish PCBooster.csproj -c Release /p:PublishProfile=SingleFileX64
dotnet publish PCBooster.csproj -c Release /p:PublishProfile=SingleFileArm64
echo 产物: out\sc-x64\PCBooster.exe  out\sc-arm64\PCBooster.exe
pause