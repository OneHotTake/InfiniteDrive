# Technical Contracts & Authority Matrix

To prevent the recurrence of duplicated intent, the following services have exclusive authority over their respective logic domains.

| Operation | Authority Service | Canonical Method |
| :--- | :--- | :--- |
| **Folder Naming** | `NamingPolicyService` | `BuildFolderName(CatalogItem)` |
| **Path Sanitization** | `NamingPolicyService` | `SanitisePath(string)` |
| **XML Escaping** | `NfoWriterService` | `SecurityElement.Escape()` |
| **Seed Metadata** | `NfoWriterService` | `WriteSeedNfo()` |
| **Enriched Metadata** | `NfoWriterService` | `WriteEnrichedNfo()` |
| **Resolution Status** | `StreamResolutionHelper`| `ResolutionResult` (Enum Status) |
| **File Persistence** | `StrmWriterService` | `WriteAsync()` / `DeleteWithVersions()` |

## Implementation Rules

### 1. Folder Naming Standard
All folder names must follow the `Title (Year) [idtype-idvalue]` format.
- **Correct:** `The Matrix (1999) [tmdbid-603]`
- **Incorrect:** `The Matrix (1999) [tmdbid=603]` (Wrong delimiter)

### 2. Resolution Result
Never return `null` or a raw `string` for stream resolution. You must return a `ResolutionResult` object. This ensures the caller knows *why* a resolution failed (e.g., "I'm throttled" vs "The file is gone").

### 3. XML Standards
Manual string concatenation or `.Replace()` chains for XML are prohibited. Use `SecurityElement.Escape()` to ensure that special characters (like ampersands in titles) do not break the Emby metadata parser.
