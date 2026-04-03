# EmbyStreams Development Server Runbook

## Overview

This runbook documents how to run an isolated Emby development server on port 9100 for testing the EmbyStreams plugin, completely separate from the production server at `/opt/emby-server`.

## Directory Structure

```
~/embyStreams/              # Main project directory
├── start-dev-server.sh     # Main startup script (run this!)
├── EmbyStreams.csproj      # Project file
├── bin/Release/net8.0/     # Build output
│   └── EmbyStreams.dll     # Compiled plugin

~/emby-local/opt/emby-server/   # Local Emby installation (DO NOT use /opt/emby-server)
├── bin/emby-server        # Wrapper script (sets up env vars)
├── system/                # EmbyServer binary and DLLs
├── lib/                   # Native libraries
└── ...

~/emby-dev-data/           # Isolated data directory for dev server
├── config/system.xml      # Server configuration (port 9100)
├── plugins/               # Plugin directory
│   └── EmbyStreams.dll   # Copied from build output
├── data/                  # Database files
├── logs/                  # Server logs
└── cache/                 # Cache directory
```

## Quick Start

Run the dev server with a single command:

```bash
cd /home/geoff/embyStreams
./start-dev-server.sh
```

The script will:
1. Build the plugin in Release mode
2. Copy `EmbyStreams.dll` to `~/emby-dev-data/plugins/`
3. Kill any existing emby processes
4. Start Emby Server on port 9100 with isolated data directory

## Access the Dev Server

- **Web UI:** http://localhost:9100
- **Plugin Config:** http://localhost:9100/web/configurationpage?name=EmbyStreams
- **Logs:** `~/emby-dev.log` or `~/emby-dev-data/logs/embyserver.txt`

## Initial Setup (One-Time)

If starting from scratch, follow these steps:

### 1. Download Emby Server

```bash
cd ~
wget https://github.com/MediaBrowser/Emby.Releases/releases/download/4.10.0.6/emby-server-deb_4.10.0.6_amd64.tar.gz
tar -xzf emby-server-deb_4.10.0.6_amd64.tar.gz
mv emby-server emby-local
```

### 2. Modify Wrapper Script

Edit `~/emby-local/opt/emby-server/bin/emby-server` to:
- Set `APP_DIR` to local path (not `/opt/emby-server`)
- Add `-updatepackage none` to prevent update checks
- Add `-port 9100` to use non-default port

```sh
#!/bin/sh

APP_DIR=/home/geoff/emby-local/opt/emby-server

export AMDGPU_IDS=$APP_DIR/extra/share/libdrm/amdgpu.ids
if [ -z "$EMBY_DATA" ]; then
  export EMBY_DATA=/var/lib/emby
fi
export FONTCONFIG_PATH=$APP_DIR/etc/fonts
export LD_LIBRARY_PATH=$APP_DIR/lib:$APP_DIR/extra/lib
export LIBVA_DRIVERS_PATH=$APP_DIR/extra/lib/dri
export OCL_ICD_VENDORS=$APP_DIR/extra/etc/OpenCL/vendors
export PATH=$APP_DIR/bin:"$PATH"
export PCI_IDS_PATH=$APP_DIR/share/hwdata/pci.ids
export SSL_CERT_FILE=$APP_DIR/etc/ssl/certs/ca-certificates.crt
export XDG_CACHE_HOME=$EMBY_DATA/cache

# Workaround for Intel drivers on kernel 6.8 and above
export NEOReadDebugKeys=1
export OverrideGpuAddressSpace=48

exec $APP_DIR/system/EmbyServer \
  -programdata $EMBY_DATA \
  -ffdetect $APP_DIR/bin/ffdetect \
  -ffmpeg $APP_DIR/bin/ffmpeg \
  -ffprobe $APP_DIR/bin/ffprobe \
  -restartexitcode 3 \
  -updatepackage none \
  -port 9100
```

### 3. Create Isolated Data Directory

```bash
mkdir -p ~/emby-dev-data/{plugins,data,logs,cache,config,metadata}
```

### 4. Configure Port in system.xml

Create or edit `~/emby-dev-data/config/system.xml` to set port 9100:

```xml
<?xml version="1.0"?>
<ServerConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <!-- ... other settings ... -->
  <PublicPort>9100</PublicPort>
  <HttpServerPortNumber>9100</HttpServerPortNumber>
  <!-- ... other settings ... -->
</ServerConfiguration>
```

**Important:** If the server is ignoring the `-port 9100` command-line argument and using port 8096 anyway, the `system.xml` config is overriding it. Ensure `HttpServerPortNumber` is set to 9100.

## Common Issues

### Server starts on port 8096 instead of 9100

**Cause:** The `system.xml` config has `HttpServerPortNumber` set to 8096, which overrides the command-line `-port` argument.

**Fix:** Edit `~/emby-dev-data/config/system.xml` and set:
```xml
<HttpServerPortNumber>9100</HttpServerPortNumber>
<PublicPort>9100</PublicPort>
```

### Segmentation fault when running EmbyServer directly

**Cause:** Trying to run `EmbyServer` binary directly without the wrapper script's environment setup.

**Fix:** Always use the wrapper script: `./bin/emby-server` instead of `./system/EmbyServer`. The wrapper sets required environment variables:
- `LD_LIBRARY_PATH`
- `FONTCONFIG_PATH`
- `LIBVA_DRIVERS_PATH`
- etc.

### Plugin not loading

**Cause:** Plugin DLL not in the correct location.

**Fix:** Ensure `EmbyStreams.dll` is copied to `~/emby-dev-data/plugins/`. The script does this automatically, but verify:
```bash
ls -la ~/emby-dev-data/plugins/EmbyStreams.dll
```

### Using wrong Emby installation

**Cause:** Accidentally using `/opt/emby-server` instead of `~/emby-local/opt/emby-server`.

**Fix:** The wrapper script and `start-dev-server.sh` must use paths under `~/emby-local/`, NOT `/opt/emby-server`. Never modify or use the production server at `/opt/emby-server`.

## Verification Commands

```bash
# Check if server is running on port 9100
ss -tlnp | grep 9100

# Check server logs
tail -f ~/emby-dev-data/logs/embyserver.txt

# Check if plugin is loaded
curl -s http://localhost:9100/web/configurationpage?name=EmbyStreams | head -5

# Kill the dev server
pkill -f "emby-server" || pkill -f EmbyServer
```

## Stopping the Server

```bash
pkill -f emby
```

Or simply run `./start-dev-server.sh` again - it will kill any existing process first.
