# Odyssey

Odyssey is a local-first desktop utility for rescuing, browsing, indexing, finding, analyzing, and managing files across accessible folders and disks. It opens in a calm guided rescue view where the user can describe anything remembered about lost work and see useful results as indexing continues. The full dual-pane commander view remains available as Expert view; each panel has its own location and path, F5 copies, and F6 moves selected items from the active panel directly to the other panel. Odyssey starts in read-only mode, and file management must be enabled explicitly in Settings.

## User experience

The default rescue view deliberately avoids database, scan-session, and forensic terminology. It asks one centered primary question, distinguishes an empty result from a failed search, and offers calm next steps instead of a terminal “zero results” state. Enter starts a search, result lists remain virtualized and lazily paged, and automatically detected drives do not move the user into Expert view. All new guidance and states are available in Czech and English with matching localization keys.

## Run

Requirements: .NET 10 on Linux or Windows.

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Odyssey.Desktop
```

Open `Odyssey.slnx` directly in JetBrains Rider. The executable startup project is `src/Odyssey.Desktop`.

## Projects

- `Odyssey.Core` — domain records, enums, and replaceable service contracts; no UI, database, or platform dependency.
- `Odyssey.Infrastructure` — portable streaming scan, bounded scan pipeline, SQLite persistence, safe file operations, disk disconnection, classification, and lazy duplicate hashing.
- `Odyssey.Search` — SQLite FTS5 schema, triggers, query construction, filters, and ranking.
- `Odyssey.Desktop` — Avalonia MVVM application, composition root, folder picker, navigation, and safe OS interactions.
- `Odyssey.Updater` — small out-of-process update helper with guarded extraction, rollback, and application restart.
- `Odyssey.Tests` — cross-platform xUnit integration and behavior tests using temporary folders and SQLite databases.

## CI, releases, and automatic updates

GitHub Actions builds and tests every push and pull request to `master` on both Linux and Windows. Test reports are retained as workflow artifacts. Dependabot checks NuGet packages and GitHub Actions weekly.

A release is produced from an existing semantic version tag in the strict form `vMAJOR.MINOR.PATCH`:

```bash
git tag -a v1.0.0 -m "Odyssey 1.0.0"
git push origin v1.0.0
```

The release workflow first runs the complete test suite, then creates self-contained single-file packages for `win-x64` and `linux-x64`. It publishes both archives, `checksums.txt`, and `update-manifest.json` into one GitHub Release. The release is kept as a draft until every asset is attached, so users never receive a partially assembled update.

Packaged builds check the official Odyssey GitHub Releases feed in the background after the main window is visible. A newer package is downloaded into the application-data directory, its SHA-256 digest is verified using a constant-time comparison, and the user is offered a calm restart action in Settings. The separate update helper waits for Odyssey to close, validates archive paths, replaces the portable installation with rollback protection, and starts Odyssey again. Development builds without the packaged helper do not perform automatic background checks. Set `ODYSSEY_DISABLE_UPDATE_CHECK=1` to disable the check explicitly.

Automatic replacement requires the extracted portable installation directory to be writable by the current user. For release integrity, enable GitHub's immutable releases option in the repository settings after confirming the release workflow: <https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases>.

## File and disk operations

Scanning and duplicate analysis remain read-only in both modes. In the default read-only mode, the operation service rejects every filesystem mutation. After the user explicitly enables file management, Odyssey can create folders, copy, move, rename, and send files or folders to the operating-system trash. Destination conflicts stop the operation instead of overwriting an existing item. Copy, move, rename, and empty-folder creation can be undone when it remains safe to do so.

Mounted secondary and removable disks are detected automatically. Supported disks can be unmounted or ejected through the operating system after confirmation. Formatting, partition editing, and permanent-delete commands are intentionally not exposed.

Odyssey stores its SQLite index and preferences under the operating system's local application-data directory. Removing a target deletes only its records from Odyssey's database; it does not delete the target directory. “Open” actions are explicit user actions delegated to the operating system.

## Performance

Directory panels load metadata asynchronously in pages of 400 items. A RAM-budgeted LRU cache makes back/forward navigation immediate, while an explicit virtualizing panel keeps the number of Avalonia controls proportional to the visible rows. Approaching the end of a panel automatically requests the next page.

Completed file operations update the SQLite/FTS index incrementally; they do not trigger a full target rescan. Full scans remain available for verification and external filesystem changes. File-copy progress is rate-limited to roughly ten UI updates per second, and copy buffers are rented from the shared array pool.

At startup Odyssey derives bounded performance limits from the available RAM and logical processor count. More capable machines receive larger directory and SQLite caches, wider bounded scan queues, larger database batches, and more workers for metadata reads and duplicate hashing. The caps deliberately leave resources for the operating system and avoid unbounded parallel disk access, which can make HDDs slower.

Search results are fetched in deterministic 250-row pages and extended lazily near the end of the virtualized list, so a large index does not create thousands of UI controls or load every match into RAM at once. Scan inserts and fingerprint updates reuse prepared SQLite commands.

Duplicate detection uses same-size grouping and a parallel xxHash64 pass for speed, but a hash match is never treated as final proof: every reported group is verified byte for byte. Current bytes are rehashed for each analysis instead of trusting an old timestamp-based cache. A completed scan also preserves previously indexed entries below paths that were unreadable during that scan, preventing temporary access errors from being reported as deleted files.

## Background automation

Odyssey runs a bounded, prioritized background queue for approved targets. Filesystem notifications are debounced and treated as hints that trigger a verification scan; watcher overflow also falls back to a scan. Scans of the same target are serialized, unavailable targets are retried, content work resumes from per-file database state, and SQLite maintenance runs at low priority. Interactive search and file work postpone content extraction and maintenance.

Content search is local and read-only. Odyssey currently extracts bounded text from plain-text and source-code formats, text-layer PDF files, DOCX, XLSX, PPTX, OpenDocument files, and archive entry names. PDF pages are parsed with PdfPig in content order. Microsoft Office Open XML files are opened through the typed, read-only Open XML SDK APIs; Word headers, footers, footnotes and endnotes, Excel shared strings and cell values, and PowerPoint slides and notes are included. ZIP, 7z, RAR, TAR, GZip, BZip2, XZ, CAB and ISO metadata is read through SharpSevenZip and the native 7-Zip engine; OpenDocument `content.xml` is extracted only into a bounded in-memory stream. Windows builds carry the matching native DLL, while Linux searches common system library locations or `ODYSSEY_7ZIP_LIBRARY`. If the native engine is unavailable, ZIP and OpenDocument retain a bounded built-in fallback and the other formats are not advertised as indexable. The source length and modification time are verified around extraction, errors are retried later, and FTS ranks filename matches above content-only matches.

Image OCR supports PNG, JPEG, TIFF, BMP, and WebP through a local Tesseract installation. Odyssey enables the image extensions only after verifying that both Czech (`ces`) and English (`eng`) language data are present, and always invokes OCR with `ces+eng`. If the executable or either language is missing, image OCR remains disabled and Settings shows the requirement instead of silently using a less accurate language configuration. Restart Odyssey after installing or changing Tesseract language data. Image-only scanned PDF pages are not OCR-rendered yet; PDF indexing currently reads their existing text layer.
