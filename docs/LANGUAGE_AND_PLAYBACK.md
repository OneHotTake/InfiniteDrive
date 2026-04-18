# Language & Playback Settings

## How InfiniteDrive Handles Languages

InfiniteDrive populates audio language and subtitle tracks from AIOStreams stream data. When you browse the version picker (long-press a title), each version shows its audio languages and available subtitles.

## Set Your Preferred Language (Recommended)

**We strongly recommend setting your preferred language in Emby.** Emby uses this to automatically select the correct audio track and subtitle track during playback — without it, Emby picks the first/default track.

### How to set it:

1. Open your Emby user profile (Settings icon → your username)
2. Go to **Display**
3. Set **Preferred metadata language** to your language (e.g., English, Japanese)
4. Set **Preferred subtitle language** if you want subtitles by default

### What this controls:

- **Audio track selection:** Emby will prefer audio tracks matching your language when multiple are available
- **Subtitle defaults:** Emby will auto-enable subtitles in your preferred language
- **Version picker sorting:** InfiniteDrive sorts versions so those with your preferred audio language appear first

## Language Fallback Priority

When sorting versions and selecting streams, InfiniteDrive uses this priority chain:

1. **User's PreferredMetadataLanguage** — your Emby user setting (per-user)
2. **Plugin's MetadataLanguage** — admin config (global default)
3. **Library's PreferredMetadataLanguage** — per-library setting in Emby admin
4. **Rank-order** — no preference, best available version first

## For Admins: Per-Library Language

If you have libraries for different languages (e.g., an anime library set to Japanese, a movies library set to English), InfiniteDrive reads each library's language from Emby's settings. This is used as a fallback when the user has no language preference set.

To configure per-library language:
1. Emby Dashboard → Libraries → click a library
2. Set **Preferred language** under the library options

## What About AIOStreams Language Data?

The audio languages and subtitles available for each stream come from the actual file content provided by AIOStreams. This is parsed from the stream metadata and cannot be controlled by InfiniteDrive settings.

## How the Version Picker Uses Language

When you see multiple versions of a movie or episode:
- Versions matching your preferred language are sorted to the top
- Audio streams matching your language are marked as default
- Emby's player uses your language preference to auto-select tracks
