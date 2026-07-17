#!/usr/bin/env bash
# Netclaw install script
#
# Usage:
#   curl -sSL https://releases.netclaw.dev/install.sh | bash
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- cli            # CLI only
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- daemon         # Daemon only
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --channel beta # Opt into prereleases
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --skip-shell   # Don't modify shell profile
#   INSTALL_DIR=/opt/netclaw curl -sSL https://releases.netclaw.dev/install.sh | bash
#
# Arguments:
#   all|cli|daemon          — Which component(s) to install (default: all)
#   --channel stable|beta   — Release channel (default: stable). 'beta' installs the
#                             newest prerelease (or latest stable if no prerelease exists).
#   --dry-run               — Resolve and report what would happen; install nothing.
#   --skip-shell            — Skip automatic shell profile modification.
#
# Environment variables:
#   INSTALL_DIR     — Install directory (default: ~/.netclaw/bin)
#   NETCLAW_VERSION — Specific version to install (overrides --channel; e.g. 0.19.0-beta.1)

set -euo pipefail

# Progress display: show curl progress bar when stderr is a terminal
if [ -t 2 ]; then
    CURL_PROGRESS=(--progress-bar)
else
    CURL_PROGRESS=(-s)
fi

# MANIFEST_URL is overridable so the script can be pointed at a local manifest
# (smoke tests) or a private mirror.
MANIFEST_URL="${MANIFEST_URL:-https://releases.netclaw.dev/manifest.json}"

# ── Argument parsing ──
COMPONENT="all"        # "all", "cli", or "daemon"
DRY_RUN=false          # --dry-run: resolve and report what would happen, install nothing
CHANNEL="stable"       # release channel: "stable" (default) or "beta" (opt into prereleases)
CHANNEL_EXPLICIT=false # true when --channel was explicitly passed
SKIP_SHELL=false       # --skip-shell: don't modify shell profile
while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) DRY_RUN=true; shift ;;
        --skip-shell) SKIP_SHELL=true; shift ;;
        --channel)
            if [ $# -lt 2 ]; then
                echo "Error: --channel requires a value (stable|beta)" >&2; exit 1
            fi
            CHANNEL="$2"; CHANNEL_EXPLICIT=true; shift 2 ;;
        --channel=*) CHANNEL="${1#*=}"; CHANNEL_EXPLICIT=true; shift ;;
        all|cli|daemon) COMPONENT="$1"; shift ;;
        *) echo "Usage: install.sh [all|cli|daemon] [--channel stable|beta] [--dry-run] [--skip-shell]" >&2; exit 1 ;;
    esac
done

# Validate channel — fail loudly on an unknown value rather than silently defaulting.
case "$CHANNEL" in
    stable|beta) ;;
    *) echo "Error: unknown channel '$CHANNEL' (expected 'stable' or 'beta')" >&2; exit 1 ;;
esac

# ── Platform detection ──
detect_platform() {
    local os arch rid

    os=$(uname -s | tr '[:upper:]' '[:lower:]')
    arch=$(uname -m)

    case "$os" in
        linux)
            case "$arch" in
                x86_64|amd64) rid="linux-x64" ;;
                aarch64|arm64) rid="linux-arm64" ;;
                *) echo "Error: Unsupported architecture '$arch' on Linux." >&2; exit 1 ;;
            esac
            ;;
        darwin)
            # A shell running under Rosetta 2 on Apple Silicon reports x86_64;
            # sysctl.proc_translated == 1 means the real CPU is arm64.
            if [ "$arch" = "x86_64" ] && \
               [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = "1" ]; then
                arch="arm64"
            fi
            case "$arch" in
                arm64) rid="osx-arm64" ;;
                x86_64)
                    echo "Error: Intel Macs are not supported. Netclaw requires" >&2
                    echo "Apple Silicon (M1 or later)." >&2
                    exit 1
                    ;;
                *) echo "Error: Unsupported architecture '$arch' on macOS." >&2; exit 1 ;;
            esac
            ;;
        *)
            echo "Error: Unsupported OS: $os. Netclaw supports Linux and macOS." >&2
            exit 1
            ;;
    esac

    echo "$rid"
}

# ── Dependency checks ──
check_deps() {
    for cmd in curl tar; do
        if ! command -v "$cmd" >/dev/null 2>&1; then
            echo "Error: Required command '$cmd' not found." >&2
            exit 1
        fi
    done
    # macOS ships 'shasum'; most Linux distros ship 'sha256sum' — accept either.
    if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
        echo "Error: Need either 'sha256sum' or 'shasum' for checksum verification." >&2
        exit 1
    fi
}

# ── SHA-256 of a file (sha256sum on Linux, shasum on macOS) ──
sha256_file() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    else
        shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

# ── JSON field extraction (no jq dependency) ──
# Uses jq if available, falls back to grep/sed
json_field() {
    local json="$1" field="$2"
    if command -v jq >/dev/null 2>&1; then
        echo "$json" | jq -r "$field"
    else
        # Simple grep/sed fallback for flat JSON fields
        echo "$json" | grep -o "\"${field#.}\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -1 | sed 's/.*: *"\(.*\)"/\1/'
    fi
}

# ── Main ──
check_deps

RID=$(detect_platform)
INSTALL_DIR="${INSTALL_DIR:-$HOME/.netclaw/bin}"

echo "Netclaw installer"
echo "  Platform: $RID"
echo "  Install dir: $INSTALL_DIR"
echo "  Channel: $CHANNEL"
if [ "$DRY_RUN" = true ]; then
    echo "  Mode: dry run (no changes will be made)"
fi
echo ""

# Fetch manifest
echo "Fetching release manifest..."
MANIFEST=$(curl -sSL --fail "$MANIFEST_URL") || {
    echo "Error: Failed to fetch manifest from $MANIFEST_URL" >&2
    exit 1
}

# Determine version. Precedence: explicit pin > channel selection > stable latest.
if [ -n "${NETCLAW_VERSION:-}" ]; then
    VERSION="$NETCLAW_VERSION"
elif [ "$CHANNEL" = "beta" ]; then
    # Beta channel resolves to latestPrerelease (the newest of {stable, prerelease}).
    VERSION=$(json_field "$MANIFEST" ".latestPrerelease")
    if [ -z "$VERSION" ] || [ "$VERSION" = "null" ]; then
        # Manifest predates the prerelease channel — use latest stable and say so
        # loudly. This is the newest known version, not a silent default.
        echo "  Note: manifest has no prerelease channel; using latest stable." >&2
        VERSION=$(json_field "$MANIFEST" ".latest")
    fi
else
    VERSION=$(json_field "$MANIFEST" ".latest")
fi

if [ -z "$VERSION" ]; then
    echo "Error: Could not determine latest version from manifest" >&2
    exit 1
fi

echo "  Version: $VERSION"
echo ""

# Parse assets using jq if available, otherwise use a simpler approach
TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT

download_component() {
    local component="$1"
    local url sha256

    if command -v jq >/dev/null 2>&1; then
        url=$(echo "$MANIFEST" | jq -r ".releases[] | select(.version==\"$VERSION\") | .assets[] | select(.component==\"$component\" and .rid==\"$RID\") | .url")
        sha256=$(echo "$MANIFEST" | jq -r ".releases[] | select(.version==\"$VERSION\") | .assets[] | select(.component==\"$component\" and .rid==\"$RID\") | .sha256")
    else
        # Fallback: extract URL and sha256 using grep (fragile but works for well-formed JSON)
        # Find the block for this component+rid
        local block
        block=$(echo "$MANIFEST" | tr '\n' ' ' | grep -oP "\"component\"\\s*:\\s*\"${component}\"[^}]*\"rid\"\\s*:\\s*\"${RID}\"[^}]*}" | head -1)
        if [ -z "$block" ]; then
            # Try reversed order
            block=$(echo "$MANIFEST" | tr '\n' ' ' | grep -oP "\"rid\"\\s*:\\s*\"${RID}\"[^}]*\"component\"\\s*:\\s*\"${RID}\"[^}]*}" | head -1)
        fi
        url=$(echo "$block" | grep -oP '"url"\s*:\s*"\K[^"]+')
        sha256=$(echo "$block" | grep -oP '"sha256"\s*:\s*"\K[^"]+')
    fi

    if [ -z "$url" ] || [ "$url" = "null" ]; then
        echo "  Warning: No $component binary found for $RID in version $VERSION" >&2
        return 1
    fi

    if [ "$DRY_RUN" = true ]; then
        echo "  DRY RUN: would install $component from $url"
        return 0
    fi

    local filename
    filename=$(basename "$url")

    echo "  Downloading $component..."
    curl "${CURL_PROGRESS[@]}" -fL -o "$TMPDIR/$filename" "$url" || {
        echo "  Error: Failed to download $url" >&2
        return 1
    }

    # Verify checksum
    echo "  Verifying checksum..."
    local actual_sha
    actual_sha=$(sha256_file "$TMPDIR/$filename")
    if [ "$actual_sha" != "$sha256" ]; then
        echo "  Error: Checksum mismatch for $filename" >&2
        echo "    Expected: $sha256" >&2
        echo "    Got:      $actual_sha" >&2
        return 1
    fi

    # Extract
    echo "  Extracting..."
    tar xzf "$TMPDIR/$filename" -C "$TMPDIR"

    # Find and install binary
    local binary_name="$component"
    local binary_path
    binary_path=$(find "$TMPDIR" -name "$binary_name" -type f | head -1)
    if [ -z "$binary_path" ]; then
        echo "  Error: Could not find $binary_name in archive" >&2
        return 1
    fi

    mkdir -p "$INSTALL_DIR"
    cp "$binary_path" "$INSTALL_DIR/$binary_name"
    chmod +x "$INSTALL_DIR/$binary_name"
    echo "  Installed $binary_name to $INSTALL_DIR/"
}

# Download requested components
SUCCESS=true
if [[ "$COMPONENT" == "all" || "$COMPONENT" == "cli" ]]; then
    download_component "netclaw" || SUCCESS=false
fi
if [[ "$COMPONENT" == "all" || "$COMPONENT" == "daemon" ]]; then
    download_component "netclawd" || SUCCESS=false
fi

if [ "$SUCCESS" = false ]; then
    echo ""
    echo "Some components failed to install." >&2
    exit 1
fi

if [ "$DRY_RUN" = true ]; then
    echo ""
    echo "Dry run complete — nothing was installed."
    exit 0
fi

# ── Persist UpdateChannel into config ──
# Only runs when --channel was explicitly passed. Without this guard a plain
# upgrade (`install.sh` with no flags) would silently overwrite an existing
# beta channel to stable — a silent fallback the project prohibits.
if [ "$CHANNEL_EXPLICIT" = true ]; then
    CONFIG_DIR="${CONFIG_DIR:-$HOME/.netclaw/config}"
    CONFIG_FILE="$CONFIG_DIR/netclaw.json"
    if [ -f "$CONFIG_FILE" ]; then
        if command -v jq >/dev/null 2>&1; then
            if jq --arg ch "$CHANNEL" '.Daemon = ((.Daemon // {}) + {UpdateChannel: $ch})' \
                "$CONFIG_FILE" > "${CONFIG_FILE}.tmp"; then
                mv "${CONFIG_FILE}.tmp" "$CONFIG_FILE"
                echo "  Set Daemon.UpdateChannel to '$CHANNEL' in $CONFIG_FILE"
            else
                rm -f "${CONFIG_FILE}.tmp"
                echo "  Warning: could not update Daemon.UpdateChannel (malformed config?)." >&2
            fi
        else
            echo "  Note: jq not found — could not set Daemon.UpdateChannel in config."
            echo "  To receive $CHANNEL updates, add to $CONFIG_FILE:"
            echo "    \"Daemon\": { \"UpdateChannel\": \"$CHANNEL\" }"
        fi
    elif [ "$CHANNEL" != "stable" ]; then
        # Fresh install: config doesn't exist yet. Write a minimal seed so
        # `netclaw init` can discover the channel preference.
        mkdir -p "$CONFIG_DIR"
        printf '{"configVersion":1,"Daemon":{"UpdateChannel":"%s"}}\n' "$CHANNEL" > "$CONFIG_FILE"
        echo "  Created $CONFIG_FILE with UpdateChannel '$CHANNEL'"
    fi
fi

# ── Shell integration ─────────────────────────────────────────────────────
# Write an intermediary env script (~/.netclaw/env) and source it from the
# user's shell RC file. The env script self-guards at runtime so duplicate
# PATH entries cannot occur even if the RC is sourced multiple times.

# Derive the parent directory of INSTALL_DIR — this is where the env script
# lives. For the default (~/.netclaw/bin) that's ~/.netclaw.
# We use dirname because the install dir may not yet exist when this block runs
# (e.g., if the user passes --skip-shell and no component needs the dir).
INSTALL_ROOT="$(dirname "$INSTALL_DIR")"
ENV_SCRIPT="$INSTALL_ROOT/env"
SOURCE_LINE=". \"$ENV_SCRIPT\""

detect_shell() {
    # $SHELL is inherited from the parent login shell — it reflects the user's
    # configured shell even when this script is piped via `curl | bash`.
    local shell_name
    shell_name="$(basename "${SHELL:-/bin/sh}")"
    echo "$shell_name"
}

get_rc_file() {
    local shell_name="$1"
    local os
    os="$(uname -s | tr '[:upper:]' '[:lower:]')"

    case "$shell_name" in
        zsh)
            echo "${ZDOTDIR:-$HOME}/.zshrc"
            ;;
        bash)
            # macOS login shells read ~/.profile; Linux interactive shells
            # read ~/.bashrc. We prefer .bashrc on Linux and .profile on macOS.
            if [ "$os" = "darwin" ]; then
                echo "$HOME/.profile"
            else
                echo "$HOME/.bashrc"
            fi
            ;;
        fish)
            local fish_conf_dir="${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d"
            echo "$fish_conf_dir/netclaw.fish"
            ;;
        *)
            echo ""
            ;;
    esac
}

write_env_script() {
    # Create the self-guarding env script. Uses a colon-affixed case guard
    # (rustup/fzf pattern) to prevent duplicate PATH entries at runtime.
    mkdir -p "$(dirname "$ENV_SCRIPT")"
    cat > "$ENV_SCRIPT" <<ENVEOF
#!/bin/sh
# netclaw shell setup
case ":\${PATH}:" in
    *:"$INSTALL_DIR":*)
        ;;
    *)
        export PATH="$INSTALL_DIR:\${PATH}"
        ;;
esac
ENVEOF
    chmod +x "$ENV_SCRIPT"
}

modify_rc_file() {
    local shell_name="$1"
    local rc_file

    rc_file="$(get_rc_file "$shell_name")"
    if [ -z "$rc_file" ]; then
        echo "  Shell '$shell_name' is not supported for automatic PATH setup."
        echo "  Add this to your shell profile:"
        echo ""
        echo "    $SOURCE_LINE"
        return 0
    fi

    # Ensure the RC file's parent directory exists (fish conf.d may not)
    mkdir -p "$(dirname "$rc_file")"

    # Touch the file so it exists — some users have no RC file yet
    touch "$rc_file"

    # Guard: check if the source line already exists
    if grep -qxF "$SOURCE_LINE" "$rc_file" 2>/dev/null; then
        echo "  Shell profile '$rc_file' already sources netclaw."
        return 0
    fi

    # Ensure a trailing newline before appending
    if [ -s "$rc_file" ] && [ "$(tail -c1 "$rc_file" | wc -l)" -eq 0 ]; then
        echo "" >> "$rc_file"
    fi

    # Append the marker comment and source line
    {
        echo "# netclaw shell setup"
        echo "$SOURCE_LINE"
    } >> "$rc_file"

    echo "  Modified '$rc_file' to add netclaw to PATH."
}

if [ "$SKIP_SHELL" = false ]; then
    SHELL_NAME="$(detect_shell)"
    echo ""
    echo "Setting up shell integration..."

    # Write env script first — it must exist before we tell the RC to source it
    write_env_script

    # Modify the RC file to source the env script
    modify_rc_file "$SHELL_NAME"

    echo ""
    echo "Installation complete! netclaw is on your PATH."
    echo ""
    echo "Start a new shell, or run:"
    echo ""
    echo "  $SOURCE_LINE"
else
    # --skip-shell was passed
    echo ""
    echo "Installation complete! (shell integration skipped)"
    echo ""
    echo "Add netclaw to your PATH by adding this to your shell profile:"
    echo ""
    echo "  $SOURCE_LINE"
    echo ""
    echo "Then restart your shell or run:"
    echo ""
    echo "  source ~/.bashrc  # or ~/.zshrc"
fi

echo ""
echo "Get started:"
echo "  netclaw init             # First-run setup wizard"
echo "  netclaw doctor           # Verify configuration"
if [ "$(uname -s)" = "Linux" ]; then
    echo "  netclaw daemon install   # Enable auto-start on boot (systemd)"
fi