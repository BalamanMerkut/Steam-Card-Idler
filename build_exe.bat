@echo off
echo ==========================================
echo Steam Card Idler - EXE Oluşturma Aracı
echo ==========================================
echo.
echo NOT: Bu scriptin çalışması için .NET 8.0 SDK gereklidir.
echo Eğer yüklü değilse: https://dotnet.microsoft.com/en-us/download/dotnet/8.0
echo.

echo [1/3] NuGet Paketleri Geri Yükleniyor...
dotnet restore SteamCardIdler.csproj

echo [2/3] Uygulama Yayınlanıyor (Tek dosya EXE)...
dotnet publish SteamCardIdler.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./Publish

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ==========================================
    echo BAŞARILI! EXE dosyanız ./Publish klasöründe hazır.
    echo ==========================================
) else (
    echo.
    echo [HATA] Derleme sırasında bir sorun oluştu.
)
pause
