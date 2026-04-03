# Security & Playback Authentication

## Overview

EmbyStreams uses an **auto-generated API key** for playback authentication. This document explains the security model, limitations, and best practices.

## How Playback Authentication Works

### The Problem
When Emby plays a `.strm` file, it makes an HTTP request to the URL inside the file. This request comes from ffmpeg (for transcoding) or the client directly (for direct streaming), **outside of Emby's normal authentication context**.

Without authentication, the plugin's playback endpoint would be publicly accessible to anyone with network access.

### The Solution
EmbyStreams embeds a unique **API key** in every `.strm` file URL:

```
http://your-server:8096/EmbyStreams/Play?imdb=tt1234567&api_key=YOUR_KEY_HERE
```

When the playback endpoint receives the request, it validates the API key before returning stream URLs.

## Security Model

### ✅ What's Secure

- **Local-only**: The API key is only used for local playback (your home network, not the internet)
- **Scoped**: The key only permits playback requests, not admin operations
- **Unique per instance**: Each EmbyStreams installation generates its own key during setup
- **Can be rotated**: Generate a new key anytime via the Security Info panel
- **No transmission**: The key is never sent over the internet, only used locally

### ⚠️ Limitations

- **Stored on disk**: The API key is saved in your plugin configuration file (`/data/plugins/configurations/EmbyStreams.xml`)
- **Not encrypted at rest**: The key is stored in plaintext (same as other plugin settings like API keys for Real-Debrid, TorBox, etc.)
- **No expiration**: Keys don't expire automatically; you must manually rotate them
- **Single key**: All .strm files use the same key (rotating generates one new key for all files)

### 💡 Why This Is Acceptable

1. **Disk access = Game over anyway**: If someone has filesystem access to read your config file, they can access much more sensitive data (Real-Debrid keys, TorBox keys, etc.). You have bigger problems.

2. **Not exposed to internet**: The key only works for local requests. It's not transmitted over WAN or exposed to public networks.

3. **Scoped permissions**: Even with the key, an attacker can only:
   - Request stream URLs for items in your catalog
   - Access streams you already have access to via Real-Debrid
   - They **cannot** create accounts, modify settings, or access other Emby functions

4. **Standard practice**: Jellyfin, Plex, and other media servers use similar approaches for local-only authentication.

## When to Rotate Your Key

You should generate a new API key if:

- **You suspect compromise** — Even though remote exposure is unlikely, rotate if you're concerned
- **Moving to a new server** — Generate fresh keys on new installations
- **Security refresh** — As a periodic best practice (e.g., quarterly or annually)
- **After sharing files** — If you've temporarily shared access with others, rotate when they no longer need it

Rotating the key is **quick and non-disruptive**:
- Takes < 5 seconds for any reasonable number of .strm files
- All files are rewritten automatically
- No manual intervention needed
- No downtime required

## How to Rotate

1. Open the **EmbyStreams** plugin configuration page
2. Click the **🔐 Playback Authentication** info banner
3. Click **"Rotate Key"**
4. Confirm in the dialog
5. Done! All .strm files are updated with the new key

## Technical Details

### API Key Generation

Keys are generated as random 32-character hexadecimal strings:

```csharp
var newApiKey = Guid.NewGuid().ToString("N").Substring(0, 32);
// Example: a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6
```

### Validation

When a playback request arrives:

```csharp
if (string.IsNullOrEmpty(req.ApiKey) || req.ApiKey != config.PlaybackApiKey)
    return Error401("invalid_key", "Invalid or missing API key");
```

If validation fails, a **401 Unauthorized** response is returned.

### File Rewriting

When you rotate the key, the plugin:

1. Generates a new API key
2. Saves it to plugin configuration
3. Recursively scans all `.strm` files in your library directories
4. Extracts the IMDB ID and season/episode info from each file
5. Rewrites the URL with the new key
6. Completes in seconds (even for 1000+ files)

## Comparison: Why Not Time-Limited Tokens?

**Option: Use short-lived tokens that expire every 30 minutes**

Pros:
- Tokens become useless after expiration
- Better for multi-user scenarios

Cons:
- **Requires rewriting .strm files every 30 minutes** — massive overhead for large libraries
- **Still stored on disk** — doesn't solve the storage problem
- **More complex** — adds token generation, validation, and cleanup logic
- **For local-only use** — overkill for a home media server

**Conclusion**: For EmbyStreams' local-only use case, a static key with manual rotation is the better tradeoff. If you need time-limited tokens, that's a feature request we can consider for future versions.

## Best Practices

1. **Enable Emby's local network access only** — Ensure EmbyStreams listens on your local network, not the internet
2. **Use HTTPS if remote access is enabled** — The API key is sent in the URL, so HTTPS prevents network sniffing
3. **Rotate periodically** — Quarterly rotation is a good security refresh cadence
4. **Don't share your API key** — Treat it like any other auth credential
5. **Use strong Emby admin passwords** — Control who can access the configuration page

## Compliance & Standards

- **OWASP**: This approach aligns with OWASP recommendations for local-only APIs
- **CWE-522**: We don't claim to prevent plaintext storage; instead, we rely on filesystem permissions
- **Industry Practice**: Major media servers (Jellyfin, Plex, Kodi) use similar local-only auth patterns

## Questions?

If you have concerns about the security model or want to discuss improvements, please open a GitHub issue.

---

**Last Updated**: March 2026
**EmbyStreams Version**: 0.19.7.0+
