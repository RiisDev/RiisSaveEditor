#!/usr/bin/env bash
# Riis Save Editor installer for Linux (x64). Installs for the current user and runs it as a
# systemd user service on http://127.0.0.1:8080 by default (--port to change).
#   ./install.sh install [version] [--port N] [--no-service]
#   ./install.sh uninstall [--remove-cache]
#   ./install.sh status
set -euo pipefail

REPO="RiisDev/RiisSaveEditor"
DIR="${RIIS_DIR:-$HOME/.local/share/riissaveeditor}"
UNIT_DIR="$HOME/.config/systemd/user"
UNIT="$UNIT_DIR/riissaveeditor.service"

# Everything except App_Data (keys, known passwords, cache) is replaced on upgrade.
clean_dir() { find "$DIR" -mindepth 1 -maxdepth 1 ! -name App_Data -exec rm -rf {} + 2>/dev/null || true; }

install_app() {
  local version="" service=1 port="" next=""
  for a in "$@"; do
    if [ "$next" = port ]; then port="$a"; next=""; continue; fi
    case "$a" in
      --no-service) service=0 ;;
      --port) next=port ;;
      *) version="${a#v}" ;;
    esac
  done
  if [ -n "$port" ] && ! { [ "$port" -ge 1 ] && [ "$port" -le 65535 ]; } 2>/dev/null; then
    echo "Port must be a number between 1 and 65535." >&2; exit 1
  fi
  if [ -z "$version" ]; then
    version=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name": *"v\{0,1\}\([^"]*\)".*/\1/p' | head -1)
    [ -n "$version" ] || { echo "Could not find the latest release." >&2; exit 1; }
  fi
  local url="https://github.com/$REPO/releases/download/v$version/RiisSaveEditor-v$version-self-contained-linux-x64.tar.gz"
  local tmp; tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
  echo "Downloading $version"
  curl -fSL "$url" -o "$tmp/app.tar.gz"

  systemctl --user stop riissaveeditor 2>/dev/null || true
  mkdir -p "$DIR"; clean_dir
  tar -xzf "$tmp/app.tar.gz" -C "$DIR"
  chmod +x "$DIR/RiisSaveEditor"
  # The tarball has no Production config, so an upgrade keeps the existing port unless --port is given.
  if [ -n "$port" ] || [ ! -f "$DIR/appsettings.Production.json" ]; then
    echo "{ \"Urls\": \"http://127.0.0.1:${port:-8080}\" }" > "$DIR/appsettings.Production.json"
  fi
  port=$(sed -n 's/.*127\.0\.0\.1:\([0-9]*\).*/\1/p' "$DIR/appsettings.Production.json")
  if [ "$service" = 1 ]; then
    mkdir -p "$UNIT_DIR"
    cat > "$UNIT" <<UNITEOF
[Unit]
Description=Riis Save Editor
After=network.target

[Service]
WorkingDirectory=$DIR
ExecStart=$DIR/RiisSaveEditor
Restart=on-failure

[Install]
WantedBy=default.target
UNITEOF
    systemctl --user daemon-reload
    systemctl --user enable --now riissaveeditor
    # Without linger a user service stops at logout and only starts at login.
    loginctl enable-linger "$USER" 2>/dev/null || echo "Note: run 'sudo loginctl enable-linger $USER' to keep it running after logout."
    echo "Installed $version, running at http://localhost:$port"
  else
    echo "Installed $version. Start it with: cd $DIR && ./RiisSaveEditor  (http://localhost:$port)"
  fi
}

uninstall_app() {
  systemctl --user disable --now riissaveeditor 2>/dev/null || true
  rm -f "$UNIT"; systemctl --user daemon-reload 2>/dev/null || true
  clean_dir
  [ "${1:-}" = "--remove-cache" ] && rm -rf "$DIR/App_Data/cache"
  rmdir "$DIR/App_Data" "$DIR" 2>/dev/null || true
  echo "Uninstalled. Anything left in $DIR/App_Data (keys, known passwords) was kept."
}

case "${1:-}" in
  install) shift; install_app "$@" ;;
  uninstall) shift; uninstall_app "$@" ;;
  status) systemctl --user status riissaveeditor --no-pager ;;
  *) sed -n 2,6p "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
