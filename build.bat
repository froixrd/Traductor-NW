@echo off
echo Compilando Traductor NWN...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
echo.
echo Listo. El ejecutable esta en la carpeta "dist\TraductorNWN.exe"
pause
