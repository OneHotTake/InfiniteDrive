# Anime library setup

Open **Plugins → InfiniteDrive → Libraries** and set the Anime library name and
folder path. The configured path must be visible and writable inside Emby's
server environment. Keep it separate from physical owned media and other managed
roots. The current Libraries page has name/path fields, not the old Catalog Sync
page's Enable Anime switch.

Choose anime sources through Providers/Sources, then run Marvin on a small sample.
Confirm the source's media type, provider identifiers and episode numbering, the
resulting STRM/NFO files, and Emby's indexed series/episodes. Metadata and artwork
preferences belong to the Emby library; configure providers actually available
on your installation. No additional anime plugin is required by InfiniteDrive's
build, and no particular third-party plugin is certified by this guide.

If a title matches incorrectly, inspect its identifiers and NFO before changing
library metadata. Native anime identifiers do not guarantee matching or complete
episode recovery. See [identity handling](anime-id-handling.md) and
[troubleshooting](troubleshooting.md). These instructions reflect source review;
they are not a new anime playback acceptance test.
