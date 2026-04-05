# EmbyStreams Development Server Runbook

## Overview

This runbook documents how to run an isolated Emby development server on port 8096 for testing the EmbyStreams plugin, using the beta version 4.10.0.8.

## Directory Structure

```
~/Projects/emby/embyStreams/     # Main project directory
├── emby-start.sh               # Main startup script (run this!)
├── emby-reset.sh               # Full reset + start script
├── emby-stop.sh                # Stop script
├── EmbyStreams.csproj          # Project file
├── bin/Release/net8.0/         # Build output
│   └── EmbyStreams.dll         # Compiled plugin

~/Projects/emby/emby-beta/      # Emby beta installation (version 4.10.0.8)
├── opt/emby-server/            # Emby Server installation
│   ├── bin/emby-server         # Wrapper script (sets up env vars)
│   ├── system/                 # EmbyServer binary and DLLs
│   └── lib/                    # Native libraries

~/emby-dev-data/                # Isolated data directory for dev server
├── config/system.xml           # Server configuration
├── plugins/                    # Plugin directory
│   └── EmbyStreams.dll        # Copied from build output
├── data/                       # Database files
├── logs/                       # Server logs
└── cache/                      # Cache directory

~/Projects/emby/emby.SDK-beta/  # Emby Plugin SDK documentation
├── Documentation/              # SDK reference docs
├── SampleCode/                 # Plugin examples
└── Resources/                  # Development resources
```

## Quick Start

Run the dev server with a single command:

```bash
cd /home/onehottake/Projects/emby/embyStreams
./emby-reset.sh
```

The script will:
1. Build the plugin in Release mode
2. Copy `EmbyStreams.dll` to `~/emby-dev-data/plugins/`
3. Kill any existing emby processes
4. Start Emby Server on port 8096 with isolated data directory

**Alternative:** Use `./emby-start.sh` for a faster start (no data wipe).

## Access the Dev Server

- **Web UI:** http://localhost:8096
- **Plugin Config:** http://localhost:8096/web/configurationpage?name=EmbyStreams
- **Logs:** `~/emby-dev.log` or `~/emby-dev-data/logs/embyserver.txt`

## Initial Setup (One-Time)

### 1. Download Emby Server Beta

```bash
cd /home/onehottake/Projects/emby
wget https://github.com/MediaBrowser/Emby.Releases/releases/download/4.10.0.8/emby-server-deb_4.10.0.8_amd64.deb
dpkg-deb -x emby-server-deb_4.10.0.8_amd64.deb emby-beta
```

### 2. Download Emby SDK Beta

```bash
cd /home/onehottake/Projects/emby
git clone https://github.com/MediaBrowser/Emby.Plugins.git emby.SDK-beta
```

### 3. Create Isolated Data Directory

```bash
mkdir -p ~/emby-dev-data/{plugins,data,logs,cache,config,metadata,transcoding-temp}
```

### 4. Configure Port (Optional)

Create or edit `~/emby-dev-data/config/system.xml` to set a custom port if needed:

```xml
<?xml version="1.0"?>
<ServerConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <PublicPort>8096</PublicPort>
  <HttpServerPortNumber>8096</HttpServerPortNumber>
</ServerConfiguration>
```

Note: The default port is 8096. The startup scripts pass `-port 8096` to override any existing config.

## Common Issues

### Server starts on wrong port

**Cause:** The `system.xml` config has a different port set, which may override the command-line `-port` argument.

**Fix:** The startup scripts pass `-port 8096` to override config, or edit `~/emby-dev-data/config/system.xml` directly.

### Segmentation fault when running EmbyServer directly

**Cause:** Trying to run `EmbyServer` binary directly without the wrapper script's environment setup.

**Fix:** The startup scripts use the wrapper script which sets required environment variables automatically. If running manually, use:
```bash
cd /home/onehottake/Projects/emby/emby-beta/opt/emby-server
./bin/emby-server -port 8096 -updatepackage none
```

### Plugin not loading

**Cause:** Plugin DLL not in the correct location.

**Fix:** Ensure `EmbyStreams.dll` is copied to `~/emby-dev-data/plugins/`. The script does this automatically, but verify:
```bash
ls -la ~/emby-dev-data/plugins/EmbyStreams.dll
```

### Using wrong Emby installation

**Cause:** Accidentally using the system-installed Emby at `/opt/emby-server`.

**Fix:** The startup scripts use relative paths to `../emby-beta/`, NOT `/opt/emby-server`. Never modify or use the production server.

## Verification Commands

```bash
# Check if server is running on port 8096
ss -tlnp | grep 8096

# Check server logs
tail -f ~/emby-dev-data/logs/embyserver.txt

# Check if plugin is loaded
curl -s http://localhost:8096/web/configurationpage?name=EmbyStreams | head -5

# Kill the dev server
./emby-stop.sh
# or
pkill -f "emby-server"
```

## Stopping the Server

```bash
./emby-stop.sh
```

Or simply run `./emby-reset.sh` again - it will kill any existing process first before restarting.
