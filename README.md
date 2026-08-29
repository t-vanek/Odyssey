# Odyssey

Odyssey is a local-first desktop utility for rescuing, browsing, indexing, finding, analyzing, and managing files across accessible folders and disks. It opens in a calm guided rescue view where the user can describe anything remembered about lost work and see useful results as indexing continues. The full dual-pane commander view remains available as Expert view; each panel has its own location and path, F5 copies, and F6 moves selected items from the active panel directly to the other panel. Odyssey starts in read-only mode, and file management must be enabled explicitly in Settings.

## User experience

The default rescue view deliberately avoids database, scan-session, and forensic terminology. It asks one centered primary question, distinguishes an empty result from a failed search, and offers calm next steps instead of a terminal “zero results” state. Enter starts a search, result lists remain virtualized and lazily paged, and automatically detected drives do not move the user into Expert view. All new guidance and states are available in Czech and English with matching localization keys.

Search ranking is presented as calm evidence bands — **High confidence**, **Good possibility**, or **Possible match** — rather than a fabricated probability percentage. The detail card explains which evidence contributed, such as the filename, path, or indexed contents. The underlying deterministic score remains available to integrations only as a ranking value, not as a statistical claim.

## Run

Requirements: .NET 10 on Linux or Windows.

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Odyssey.Desktop
```

Open `Odyssey.slnx` directly in JetBrains Rider. The executable startup project is `src/Odyssey.Desktop`.

### Product stability scenarios

The `Stability` test category exercises complete user journeys against the real temporary filesystem, SQLite/FTS index, scanner, background content indexer, search service, and guarded file operations. It covers a service restart with preserved content search, a 1,250-file paged catalogue, concurrent searches during a changing rescan, a temporarily unavailable drive, and a copy/rename/undo workflow that keeps the index synchronized.

Run the scenarios locally with:

```bash
dotnet test Odyssey.slnx --filter "Category=Stability"
```

Normal CI runs the full suite and then repeats these scenarios three times on both Linux and Windows. The repeated pass is intentional: it is designed to expose races, leaked file handles, nondeterministic paging, and SQLite concurrency failures before a release.

The complete automated gates and the real removable-drive, process-crash, suspend/resume, low-disk-space, large-copy, and update acceptance checklist are documented in [Product stability scenarios](docs/stability-scenarios.md).

## Projects

- `Odyssey.Core` — domain records, enums, and replaceable service contracts; no UI, database, or platform dependency.
- `Odyssey.Infrastructure` — portable streaming scan, bounded scan pipeline, SQLite persistence, safe file operations, disk disconnection, classification, and lazy duplicate hashing.
- `Odyssey.Search` — SQLite FTS5 schema, triggers, query construction, filters, and ranking.
- `Odyssey.Desktop` — Avalonia MVVM application, composition root, folder picker, navigation, and safe OS interactions.
- `Odyssey.Agent` — model-independent operation plans, guard pipeline, approval queue, execution and audit.
- `Odyssey.Mcp` — MCP executor for local stdio clients and opt-in authenticated Streamable HTTP.
- `Odyssey.Updater` — small out-of-process update helper with guarded extraction, rollback, and application restart.
- `Odyssey.Tests` — cross-platform xUnit integration and behavior tests using temporary folders and SQLite databases.

## CI, releases, and automatic updates

GitHub Actions builds every push and pull request to `master` on both Linux and Windows with warnings promoted to errors. The full suite records TRX and coverage output, stability journeys run three additional times, hung tests are bounded, and a machine-readable audit fails on any known vulnerable direct or transitive NuGet package. Linux CI also builds the pinned 7-Zip native source and runs the real native-format tests against that exact library. Workflow actions are pinned to immutable full commit SHAs and jobs have explicit time limits. Test, audit, and native-build digest reports are retained as workflow artifacts. Dependabot checks NuGet packages and GitHub Actions weekly.

A stable release is produced from an existing tag in the strict form `vMAJOR.MINOR.PATCH`:

```bash
git tag -a v1.0.0 -m "Odyssey 1.0.0"
git push origin v1.0.0
```

Four-part preview builds use the exact form `MAJOR.MINOR.PATCH.REVISION-preview`; the first planned preview is `0.0.0.1-preview`. Such a release is marked as a GitHub prerelease, while its .NET assembly and update-comparison version retain all four numeric components. Preview releases are deliberately excluded from the stable automatic-update feed exposed by GitHub's `releases/latest` endpoint and must be installed manually.

The release workflow accepts only one of these tag forms whose commit is reachable from `master`, reruns the vulnerability/build/test/stability gates, and restores each runtime explicitly before creating self-contained single-file packages for `win-x64` and `linux-x64`. It builds Linux 7-Zip from an exact reviewed source commit, verifies the package-supplied Windows binary by SHA-256, removes wrong-platform binaries, and stages the matching native library, licenses, notices, and `native-assets.json` into each archive. The completed archives are extracted and their declared native hashes are checked again before publishing the archives, `checksums.txt`, and `update-manifest.json` into one GitHub Release. The release remains a draft until every checked asset is attached; a failed run can refresh that draft but cannot overwrite an already published release. See [Native dependencies and release packaging](docs/native-dependencies.md).

Packaged builds check the official Odyssey GitHub Releases feed in the background after the main window is visible. A newer package is downloaded into the application-data directory, its SHA-256 digest is verified using a constant-time comparison, and the user is offered a calm restart action in Settings. The separate update helper waits for Odyssey to close, validates archive paths, replaces the portable installation with rollback protection, and starts Odyssey again. Development builds without the packaged helper do not perform automatic background checks. Set `ODYSSEY_DISABLE_UPDATE_CHECK=1` to disable the check explicitly.

Automatic replacement requires the extracted portable installation directory to be writable by the current user. For release integrity, enable GitHub's immutable releases option in the repository settings after confirming the release workflow: <https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases>.

## MCP executor and AI approvals

Odyssey acts as the executor while GPT, Claude, Codex, or another MCP client acts as the orchestrator. AI clients can search the local index and propose typed file operations, but they cannot approve their own requests. Every write is stored as an immutable SHA-256-bound plan, evaluated by guards, shown in the Odyssey approval center, revalidated immediately before execution, and written to the local operation audit.

The default AI access level is read-only. Settings can enable file management inside indexed locations or full access with strong guards for paths outside those locations. Odyssey application data and virtual kernel/device paths remain blocked in every mode. See [MCP setup and security](docs/mcp.md) for client configuration and the complete tool list.

## File and disk operations

The two panels now include independent persistent tabs and a persistent editable favorite-directory hotlist. Every tab keeps its own target, path, back/forward history and filter state; unavailable restored paths stay visible but are never opened outside their validated indexed target. Per-panel, case-configurable contains/glob/regex quick filters are evaluated over the complete cached directory snapshot before the 400-row UI page is produced, so matches beyond the first page remain discoverable. Commander selection commands (all, invert, same extension, mask, restore previous), tab commands, and F2/F3–F8 shortcuts always resolve the active panel. See [Keyboard shortcuts](docs/keyboard-shortcuts.md). F3 opens a selected supported archive as a panel location; for ordinary files it still opens the OS-associated application because bounded Quick View is not implemented yet.

ZIP archives can always be browsed in normal tabs, including history, paging and quick filters. Selected local items can be turned into one new validated ZIP from the toolbar. F5 extracts archive entries to a local panel, adds local items to an opened ZIP, or copies between two archives through a bounded relay under Odyssey application data. Existing ZIP entries follow the selected Fail/Skip/KeepBoth/Replace policy; F8 can remove reviewed entries after a separate warning. Every ZIP change writes and validates a complete sibling archive before replacing the original. A durable, path-validated recovery journal reconciles interrupted publication before the transfer worker starts, while stale GUID-named archive relays are removed without following links. ZIP writes currently use the uncompressed store method so Odyssey does not create output that violates its own archive-bomb ratio guard. Archive changes survive crash reconciliation but do not yet have user-facing Undo.

Official `win-x64` and `linux-x64` release archives carry a verified, replaceable 7-Zip 26.02 native library, so read-only browsing/extraction is advertised for 7z, TAR, TAR.GZ, GZip, BZip2, XZ and RAR when that library loads successfully. Development builds can use a compatible system installation or the explicit `ODYSSEY_7ZIP_LIBRARY` path. Creating or modifying those formats and previewing an unextracted entry are not yet implemented; see [the Commander modernization roadmap](docs/commander-roadmap.md).

Scanning and duplicate analysis remain read-only in both modes. In the default read-only mode, the operation service rejects every filesystem mutation. After the user explicitly enables file management, Odyssey can create folders, copy, move, rename, and send files or folders to the operating-system trash. Desktop copy and move requests enter a durable transfer queue that survives application restarts and supports pause, resume, cancel, and retry. The user can choose whether destination conflicts stop, skip the item, keep both names, or safely replace the destination. Replacements preserve the previous destination for undo, and queued copies can be SHA-256 verified before their temporary `.odyssey-part` item is atomically published. Copy, move, rename, and empty-folder creation can be undone when it remains safe to do so.

The Compare & sync page compares the current left and right panel directories recursively. Its fast mode uses type, size, and modification time; content mode verifies equal-sized files with SHA-256. Symbolic links are compared as links and never traversed. One-sided directory trees are collapsed into one reviewable action, identical entries can be hidden, and selected left-to-right or right-to-left changes are submitted to the durable transfer queue. Synchronization is preview-first and additive: it copies the reviewed side and safely replaces an existing counterpart, but never deletes unpaired files from the other side.

Current and planned synchronization modes are documented in [Directory synchronization modes](docs/synchronization.md). SFTP identity and credential behavior is documented in [Remote connections and credentials](docs/remote-connections.md); archive and remote safety boundaries are documented in [Archive and remote-operation security model](docs/security-archives-remote.md).

The SFTP page provides a session-only remote connection and browser. A first connection without an expected host-key fingerprint is rejected after displaying the server's presented SHA-256 fingerprint; the user must verify and enter that fingerprint before reconnecting. Passwords remain in memory and are never written to settings or transfer-queue state. Selected local files and directories can be uploaded, and selected remote entries can be downloaded, through the same durable queue and read-only access gate. Transfers use temporary names, verify file contents with SHA-256 when enabled, avoid following symbolic links, and restore a replaced destination if the transfer fails. Remote replacement backups are removed after a successful transfer, so remote operations do not currently participate in local Undo history.

Mounted secondary and removable disks are detected automatically. Supported disks can be unmounted or ejected through the operating system after confirmation. Formatting, partition editing, and permanent-delete commands are intentionally not exposed.

Odyssey stores its SQLite index and preferences under the operating system's local application-data directory. Removing a target deletes only its records from Odyssey's database; it does not delete the target directory. “Open” actions are explicit user actions delegated to the operating system.

## Performance

Directory panels load metadata asynchronously in pages of 400 items. A RAM-budgeted LRU cache makes back/forward navigation immediate, while an explicit virtualizing panel keeps the number of Avalonia controls proportional to the visible rows. Approaching the end of a panel automatically requests the next page.

Completed file operations update the SQLite/FTS index incrementally; they do not trigger a full target rescan. Full scans remain available for verification and external filesystem changes. File-copy progress is rate-limited to roughly ten UI updates per second, copy buffers are rented from the shared array pool, and queue state is written atomically to application data after every durable state transition.

Every scan persists a lightweight checkpoint after durable database batches. If Odyssey or the operating system stops unexpectedly, the next launch marks the abandoned attempt as interrupted, shows a calm recovery state, and automatically starts a new verification scan linked to the interrupted attempt. Already committed index batches remain searchable. The recovery scan deliberately verifies the complete approved target instead of trusting a potentially stale filesystem cursor, so speed never weakens result correctness.

At startup Odyssey derives bounded performance limits from the available RAM and logical processor count. More capable machines receive larger directory and SQLite caches, wider bounded scan queues, larger database batches, and more workers for metadata reads and duplicate hashing. The caps deliberately leave resources for the operating system and avoid unbounded parallel disk access, which can make HDDs slower.

Search results are fetched in deterministic 250-row pages and extended lazily near the end of the virtualized list, so a large index does not create thousands of UI controls or load every match into RAM at once. Scan inserts and fingerprint updates reuse prepared SQLite commands.

Duplicate detection uses same-size grouping and a parallel xxHash64 pass for speed, but a hash match is never treated as final proof: every reported group is verified byte for byte. Current bytes are rehashed for each analysis instead of trusting an old timestamp-based cache. A completed scan also preserves previously indexed entries below paths that were unreadable during that scan, preventing temporary access errors from being reported as deleted files.

## Background automation

Odyssey runs a bounded, prioritized background queue for approved targets. Filesystem notifications are debounced and treated as hints that trigger a verification scan; watcher overflow also falls back to a scan. Scans of the same target are serialized, unavailable targets are retried, interrupted scans are recovered before routine work, content work resumes from per-file database state, and SQLite maintenance runs at low priority. Interactive search and file work postpone content extraction and maintenance.

Content search is local and read-only. Odyssey currently extracts bounded text from plain-text and source-code formats, text-layer PDF files, DOCX, XLSX, PPTX, OpenDocument files, and archive entry names. PDF pages are parsed with PdfPig in content order. Microsoft Office Open XML files are opened through the typed, read-only Open XML SDK APIs; Word headers, footers, footnotes and endnotes, Excel shared strings and cell values, and PowerPoint slides and notes are included. ZIP, 7z, RAR, TAR, GZip, BZip2, XZ, CAB and ISO metadata is read through SharpSevenZip and the native 7-Zip engine; OpenDocument `content.xml` is extracted only into a bounded in-memory stream. Packaged Windows and Linux releases carry the matching verified library. Development builds also search compatible system locations or `ODYSSEY_7ZIP_LIBRARY`; a candidate must load and expose the expected 7-Zip ABI before it is selected. If the native engine is unavailable, ZIP and OpenDocument retain a bounded built-in fallback and the other formats are not advertised as indexable. The source length and modification time are verified around extraction, errors are retried later, and FTS ranks filename matches above content-only matches.

Image OCR supports PNG, JPEG, TIFF, BMP, and WebP through a local Tesseract installation. Odyssey enables the image extensions only after verifying that both Czech (`ces`) and English (`eng`) language data are present, and always invokes OCR with `ces+eng`. If the executable or either language is missing, image OCR remains disabled and Settings shows the requirement instead of silently using a less accurate language configuration. Restart Odyssey after installing or changing Tesseract language data. Image-only scanned PDF pages are not OCR-rendered yet; PDF indexing currently reads their existing text layer.
