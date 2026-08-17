#!/usr/bin/env bash
# ============================================================================
#  Manchester School of Modelling — start the website
#
#  The macOS and Linux equivalent of "Start Website.bat". Run it with:
#      ./start-website.sh
#  Stop the site with Ctrl+C.
# ============================================================================
set -u

cd "$(dirname "$0")"

echo
echo "  MANCHESTER SCHOOL OF MODELLING"
echo "  ------------------------------"
echo

if ! command -v dotnet >/dev/null 2>&1; then
    echo "  .NET is not installed on this computer."
    echo
    echo "  Install the .NET 10 SDK — the SDK, not the Runtime — from:"
    echo "      https://dotnet.microsoft.com/download/dotnet/10.0"
    echo
    exit 1
fi

# The runtime alone can start a finished application but cannot build one, and
# the download page offers both. This is the usual reason "I installed .NET"
# still does not work.
if [ -z "$(dotnet --list-sdks 2>/dev/null)" ]; then
    echo "  .NET is installed, but only the part that runs finished programs."
    echo "  This project also needs the SDK to build itself."
    echo
    echo "  Install the .NET 10 SDK from:"
    echo "      https://dotnet.microsoft.com/download/dotnet/10.0"
    echo
    exit 1
fi

echo "  Starting the website. The first time takes about a minute."
echo "  Leave this window open while you use the site, and press Ctrl+C to stop."
echo

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://localhost:5213

# Open the browser once the site is actually answering, rather than a moment
# too early — a page opened before Kestrel is listening just shows an error.
(
    for _ in $(seq 1 60); do
        if curl -fsS -o /dev/null "http://localhost:5213/health" 2>/dev/null; then
            command -v open >/dev/null 2>&1 && open "http://localhost:5213/models" && exit 0
            command -v xdg-open >/dev/null 2>&1 && xdg-open "http://localhost:5213/models" && exit 0
            echo
            echo "  The site is ready at: http://localhost:5213/models"
            exit 0
        fi
        sleep 2
    done
) &

dotnet run --project "src/Msm.Portfolio.Web"
