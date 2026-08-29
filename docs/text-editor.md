# Safe text editing

F4 opens one regular local text file selected in the active file panel. Odyssey embeds Monaco Editor as checked, offline application assets; it does not load editor code, fonts, telemetry, or file content from a CDN. Monaco is a text editor component rather than a VS Code extension host, so VS Code extensions are neither installed nor executed.

## Supported files and controls

- UTF-8 with or without BOM and UTF-16 little- or big-endian with BOM are detected strictly and preserved on save.
- Common source and configuration extensions receive Monaco language services and syntax highlighting. Unknown text extensions remain editable as plain text.
- A document is limited to 8 MiB. Larger files and content that appears binary remain available through bounded Quick View or an external application rather than being loaded into the editor.
- `Ctrl+S` saves, `Escape` requests close, and closing dirty content requires an explicit discard confirmation. F2–F8 cannot escape the editor overlay and act on a hidden panel.
- Read-only mode opens the document without mutation capability. Switching to file-management mode is required before saving.

If the embedded WebView runtime is unavailable, Odyssey keeps the file untouched, displays the failure, and offers an explicit external-editor action. On Windows the Avalonia WebView uses WebView2. Linux needs a supported WPE WebKit or WebKitGTK runtime supplied by the distribution. This fallback is a capability boundary, not a silent security bypass.

## Save and conflict model

Opening canonicalizes the path, requires a regular local file, rejects symbolic-link/reparse-point leaves and ancestors, bounds the byte length, and binds the editor session to length, modification time, and SHA-256 content. Before saving, Odyssey recomputes that version; an externally changed document is never overwritten and must be reloaded or opened externally.

A save encodes into a unique sibling temporary file, flushes it to durable storage, preserves supported Unix permissions, hashes the staged result, and revalidates the original. Publication uses filesystem replacement with a sibling backup. Odyssey verifies the published hash before deleting that backup; a caught publication or verification failure restores the original. Cancellation before publication leaves the original unchanged. The editor does not write through a WebView download, browser filesystem API, shell command, or uncontrolled temporary directory.

## Embedded-content boundary

The desktop host exposes only a random, per-process loopback URL. Before listening it validates a generated allowlist manifest and the SHA-256 digest and size of every bundled asset. Requests outside the random route, unknown files, traversal attempts, and modified assets are rejected. Responses disable caching and MIME sniffing, while the page applies a restrictive Content Security Policy with no network connection destination.

The native bridge accepts only bounded JSON messages from the exact editor origin. The page receives the display name, language, read-only state and bounded text content, but not the filesystem path. It can request save/close and return edited content; all filesystem decisions remain in the infrastructure service. DevTools, popups and navigation away from the editor origin are disabled.

The reproducible asset build lives in `src/Odyssey.Desktop/Monaco`. CI installs the lockfile with lifecycle scripts disabled, audits all npm dependencies down to low severity, rebuilds the assets and rejects any uncommitted difference. The packaged asset set includes Monaco's license and third-party notices.
