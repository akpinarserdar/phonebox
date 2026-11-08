#!/usr/bin/env bash
# PhoneBox installer for Linux and macOS.
#
#   ./scripts/install.sh                 # build and install the agent, then start it
#   ./scripts/install.sh --autostart     # ... and start it at login (systemd user service / launchd agent)
#   ./scripts/install.sh --apk           # ... and install the panel app on the connected phone
#   ./scripts/install.sh --serial <id>   # pick the phone when several devices are connected
#
# Needs the .NET 10 SDK (https://dotnet.microsoft.com/download) and adb:
#   Ubuntu/Debian: sudo apt install adb       Fedora: sudo dnf install android-tools       macOS: brew install android-platform-tools
# Optional, for more widgets on Linux: playerctl (now playing), wireplumber or pulseaudio-utils (volume), xprintidle.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
autostart=0; apk=0; serial=""
while [ $# -gt 0 ]; do
  case "$1" in
    --autostart) autostart=1 ;;
    --apk) apk=1 ;;
    --serial) serial="$2"; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  *) echo "Unsupported system: $(uname -s). On Windows use scripts/install.ps1." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "Unsupported CPU: $(uname -m)" >&2; exit 1 ;;
esac
rid="$os-$arch"

command -v dotnet >/dev/null || { echo ".NET 10 SDK is required: https://dotnet.microsoft.com/download" >&2; exit 1; }
if [ -z "${DOTNET_ROOT:-}" ]; then
  DOTNET_ROOT="$(dotnet --list-runtimes | sed -n '1s/.*\[\(.*\)\/shared\/.*\]/\1/p')"
  [ -n "$DOTNET_ROOT" ] && export DOTNET_ROOT
fi

echo "==> Building the agent ($rid)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
dotnet publish "$root/agent/PhoneBox.Agent" -c Release -f net10.0 -r "$rid" --self-contained false -o "$tmp" --nologo -v q
app="$("$tmp/PhoneBoxAgent" --print-install-dir)"

echo "==> Stopping a running agent"
if [ "$os" = linux ] && command -v systemctl >/dev/null; then systemctl --user stop phonebox.service 2>/dev/null || true; fi
if [ "$os" = osx ]; then launchctl unload "$HOME/Library/LaunchAgents/net.oxog.phonebox.agent.plist" 2>/dev/null || true; fi
pkill -x PhoneBoxAgent 2>/dev/null || true
sleep 0.5

echo "==> Installing to $app"
mkdir -p "$app"
cp -R "$tmp/." "$app/"
chmod +x "$app/PhoneBoxAgent"

if [ "$autostart" = 1 ]; then
  if [ "$os" = linux ]; then
    echo "==> Adding a systemd user service (starts at login)"
    unit="$HOME/.config/systemd/user/phonebox.service"
    mkdir -p "$(dirname "$unit")"
    cat > "$unit" <<EOF
[Unit]
Description=PhoneBox agent (desk panel for an Android phone)
After=graphical-session.target

[Service]
ExecStart=$app/PhoneBoxAgent
Restart=on-failure
RestartSec=5
Environment=DOTNET_ROOT=$(dirname "$(readlink -f "$(command -v dotnet)")")

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload
    systemctl --user enable --now phonebox.service
  else
    echo "==> Adding a launchd agent (starts at login)"
    plist="$HOME/Library/LaunchAgents/net.oxog.phonebox.agent.plist"
    mkdir -p "$(dirname "$plist")"
    cat > "$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>net.oxog.phonebox.agent</string>
  <key>ProgramArguments</key><array><string>$app/PhoneBoxAgent</string></array>
  <key>EnvironmentVariables</key><dict>
    <key>PATH</key><string>/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>
    <key>DOTNET_ROOT</key><string>$DOTNET_ROOT</string>
  </dict>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
</dict></plist>
EOF
    launchctl load "$plist"
  fi
else
  echo "==> Starting the agent"
  nohup "$app/PhoneBoxAgent" >/dev/null 2>&1 &
fi

if [ "$apk" = 1 ]; then
  adb="$(command -v adb || true)"
  [ -n "$adb" ] || { echo "adb not found (see the top of this script)" >&2; exit 1; }
  # the newest of a release APK in dist/ and the last local build
  apkfile="$(ls -t "$root"/dist/PhoneBox-*.apk "$root/android/app/build/outputs/apk/release/app-release.apk" 2>/dev/null | head -n1 || true)"
  if [ -z "$apkfile" ]; then
    echo "==> Building the APK (needs the Android SDK)"
    (cd "$root/android" && chmod +x gradlew && ./gradlew assembleRelease --console=plain -q)
    apkfile="$root/android/app/build/outputs/apk/release/app-release.apk"
  fi
  echo "==> Installing $(basename "$apkfile") on the phone"
  if [ -n "$serial" ]; then
    "$adb" -s "$serial" install -r "$apkfile"
    "$adb" -s "$serial" shell am start -n net.oxog.phonebox.deskpanel/.MainActivity >/dev/null
  else
    "$adb" install -r "$apkfile"
    "$adb" shell am start -n net.oxog.phonebox.deskpanel/.MainActivity >/dev/null
  fi
fi

echo "PhoneBox is running. Settings: http://127.0.0.1:8765"
