# Version Slots & URL Rewriting

## 1. The Versioning Strategy
We use Emby’s native versioning support. For any movie `Title.strm`, we may have `Title - 4K.strm` and `Title - 1080p.strm`.

## 2. URL Rewriting (The Proxy Path)
To keep `.strm` files valid even when provider tokens change:
- **Stored URL:** The `.strm` file contains a local loopback URL (e.g., `http://localhost:8096/InfiniteDrive/resolve?id=XYZ`).
- **Dynamic Resolution:** When the user hits "Play," `StreamEndpointService` resolves the *current* provider URL, signs it with HMAC, and redirects the player.
- **Benefit:** We never have to "Rewrite" 10,000 files just because a provider changed their domain or API key.

## 3. Rehydration
If a user changes their "Preferred Quality" in settings:
1. `VersionMaterializer` scans the DB for the new preferred slots.
2. It triggers a batch `WriteAsync` for the missing versions.
3. This follows the **Optimistic** pattern (write first, validate later).
