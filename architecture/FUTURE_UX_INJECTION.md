# Design Document: The Infinite Bridge
**Project Status:** Future Architecture / R&D
**Model:** Proxy-Level UI Injection (The "Heart of Gold" Protocol)

## 1. Executive Summary
The **Infinite Bridge** is a hybrid architectural approach that bypasses the limitations of the Emby plugin system by moving the logic to the Network Layer (Nginx/njs). Instead of populating the Emby database with millions of static `.strm` files, we inject dynamic stream-selection UI directly into the native client detail views.

## 2. The UX Blueprint (Apple-Inspired)
We move from a "Management" mental model to a "Dynamic Discovery" model. On native Apple TV and Android TV apps, the user is presented with a horizontal row of curated stream choices.

### 2.1 Native Client UI Mockup
```text
+-------------------------------------------------------------+
|  THE MATRIX (1999)                                          |
|  98% Match  |  2h 16m  |  4K HDR  |  Sci-Fi                 |
|                                                             |
|  [ PLAY ]   [ TRAILER ]   [ + WISHLIST ]                    |
|                                                             |
|  CHOOSE YOUR VERSION (Injected via Infinite Bridge)         |
|  +----------------+  +----------------+  +----------------+ |
|  |  4K REMUX      |  |  1080P HDR     |  |  720P COMPACT  | |
|  |  84.2 GB       |  |  12.5 GB       |  |  2.1 GB        | |
|  |  [ Dolby Atm ] |  |  [ Surround ]  |  |  [ Stereo ]    | |
|  +----------------+  +----------------+  +----------------+ |
|   ( Focused State )                                         |
+-------------------------------------------------------------+
```

## 3. Technical Architecture

### 3.1 The Interceptor (Nginx + njs)
- **Hook:** Intercepts `GET /Items/{Id}` (Metadata) and `GET /Items/{Id}/PlaybackInfo`.
- **Logic:** Extracts TMDB/IMDb IDs from the JSON response.
- **Async Action:** Queries the AIOStreams resolver in the background while the UI loads.

### 3.2 The UI Injection
- **Injection Method:** Uses `js_body_filter` in Nginx to append HTML/JS to the Emby client response.
- **Styling:** Injected elements use native Emby CSS classes (`detailButton`, `raised`, etc.) to ensure the OS-level focus engine (Siri Remote / D-Pad) handles navigation seamlessly.

### 3.3 The Self-Healing Resolver
- **Probing:** The proxy performs a `HEAD` request to the Debrid provider before rendering a button.
- **Fallback:** If a stream is unresponsive, the button is omitted. This ensures a 0% failure rate when a user clicks a choice.

## 4. Why This Approach?

| Feature | Infinite Bridge (Proxy) | Standard Plugin (.strm) |
| :--- | :--- | :--- |
| **Database Bloat** | Zero. | High (Millions of files). |
| **User Agency** | Selection happens at playback. | Selection is automated/fixed. |
| **Maintenance** | Self-healing links. | Requires library re-scans. |
| **Native Apps** | Works with official player. | Often requires external players. |

## 5. Implementation Roadmap

### Phase 1: The "Silent" Interceptor
Implement the Nginx njs script to log Item IDs and verify that we can fetch AIOStreams data before the client finishes rendering the page.

### Phase 2: Action Button Injection
Inject a single "Search AIO" button into the detail page that opens a basic list of links.

### Phase 3: The Native Row
Refine the CSS/HTML injection to mirror the native Apple TV/Android TV button row, implementing focus-state parity.

---
*“Don’t Panic. The towel is in the reverse proxy.”*
