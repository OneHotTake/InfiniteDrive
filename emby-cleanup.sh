#!/bin/bash
#
# InfiniteDrive Emby Cleanup Script
# Deletes .strm files, plugin databases, and configuration
# Use when you need to start fresh without a full server reset
#

set -e

echo "=== InfiniteDrive Cleanup ==="

# 1. Delete .strm files
echo ""
echo "Deleting .strm files..."
find /media/infinitedrive -name "*.strm" -type f -delete 2>/dev/null || true
find /media/infinitedrive -name "*.nfo" -type f -delete 2>/dev/null || true
find /media/infinitedrive -type d -empty -delete 2>/dev/null || true
echo "✓ Deleted .strm and .nfo files"

# 2. Delete plugin databases
echo ""
echo "Deleting plugin databases..."
rm -rf ~/emby-dev-data/data/InfiniteDrive/* 2>/dev/null || true
echo "✓ Deleted plugin databases"

# 3. Delete plugin configuration
echo ""
echo "Deleting plugin configuration..."
rm -f ~/emby-dev-data/plugins/configurations/InfiniteDrive.xml 2>/dev/null || true
echo "✓ Deleted plugin configuration"

# 4. Delete emby databases
echo ""
echo "Deleting emby databases..."
rm -rf ~/emby-dev-data/data/* 2>/dev/null || true
echo "✓ Deleted emby databases"


# 4. Summary
echo ""
echo "=== Cleanup Complete ==="
echo "  • .strm files: deleted"
echo "  • .nfo files: deleted"
echo "  • Plugin databases: deleted"
echo "  • Plugin config: deleted"
echo ""
echo "Next steps:"
echo "  1. Restart Emby: ./emby-start.sh"
echo "  2. Configure plugin in web UI"
echo "  3. Run catalog sync"
