# InfiniteDrive settings design

**Design principle:** Apple-simple intent, sensible defaults, state-driven
behavior.

## Tab order

1. **Overview** — readiness and setup guidance.
2. **Libraries** — three names and paths. Language, artwork, and certification
   follow Emby.
3. **Quality** — desired-version buckets plus **Allow REMUX** and **Allow CAM/TS**.
4. **Providers** — Manifest 1 and optional Manifest 2. Both are active peers.
5. **Sources** — manifest catalogs, source credentials, system lists, and user
   lists. Lists remain functional when a manifest has no catalogs.
6. **Restrictions** — Discover/search parental intent and block list.
7. **Marvin** — automatic-state explanation and **Run Marvin Now** only.
8. **Advanced** — logging, cache maintenance, rebuild, and reset controls.

## Derived behavior

- A configured manifest is active; an empty field is inactive. No enable-backup
  rule duplicates that state.
- Manifest catalogs and list catalogs are unioned and duplicate IDs collapse.
- With no usable catalog or active list, a small Cinemeta starter is derived for
  the current sync so a stream-only manifest cannot create an empty library.
- Emby supplies metadata language, image language, and certification country.
- Provider backoff and observed cache expiry govern request pressure.
- Playlists and watched state protect pruning automatically.
- Physical owned media wins over InfiniteDrive-managed virtual duplicates.
  External virtual systems, including Mycelium, never drive behavior.
- The next released, indexed Emby episode is pre-warmed; future placeholders are
  ignored.
- Distinct editions are retained first within Emby's eight-version ceiling.
- REMUX and CAM/TS remain explicit opt-ins because they express user intent, not
  discoverable state.

Saving a tab triggers background reconciliation when its intent changed.
