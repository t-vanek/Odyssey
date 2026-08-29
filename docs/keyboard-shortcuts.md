# Keyboard shortcuts

Commander shortcuts work on the active file panel only. The highlighted panel is active; selecting an item also activates its panel.

| Shortcut | Action |
|---|---|
| `F2` | Rename the single selected item |
| `F3` | Open bounded Quick View for the selected local/archive file, or the single selected file on the Remote page |
| `F4` | Edit the selected local file using the operating-system association |
| `F5` | Copy active-panel selection to the other panel, including local↔ZIP and controlled archive→archive transfer |
| `F6` | Move active-panel selection to the other panel after confirmation |
| `F7` | Create a directory in the active panel |
| `F8` | Move local selection to trash; in a writable ZIP, remove reviewed entries through a full transactional rewrite |
| `Ctrl+F` | Focus the active panel quick filter |
| `Escape` | Clear the active quick filter |
| `Ctrl+A` | Select all visible filtered items |
| `Ctrl+I` | Invert the visible selection |
| `Ctrl+E` | Select visible items with the focused item's extension |
| `Ctrl+M` | Select by a mask using the panel's current contains/glob/regex mode |
| `Ctrl+Shift+M` | Open Multi-Rename for the active panel selection |
| `Ctrl+Shift+R` | Restore the previous visible selection |
| `Ctrl+T` | Open a new tab in the active panel |
| `Ctrl+Shift+D` | Duplicate the active tab, including its history and filter |
| `Ctrl+W` | Close the active tab (the last tab in a panel cannot be closed) |
| `Ctrl+Shift+T` | Reopen the most recently closed tab in the active panel |

Glob filters support `*`, `?`, and semicolon-separated alternatives such as `*.cs;*.md`. Regex matching is timeout-bounded. An invalid pattern is shown as an error and does not replace the last valid result view. Match-case is configured independently in each panel.

F4 behavior depends on a configured OS file association. On Windows Odyssey asks for the `edit` verb and falls back to the default association if that verb is unavailable. Odyssey passes the selected path as a process argument; it does not construct a shell command string.

Inside Quick View, `Page Up` and `Page Down` load the previous or next bounded byte block. `Ctrl+Home` and `Ctrl+End` jump to the first or last block, and `Escape` closes the viewer. These keys are captured by the viewer, so file mutation shortcuts cannot run against a panel hidden behind it. Known text extensions are syntax-highlighted while remaining selectable; forced Hex and unknown formats use the plain viewer. Opening a directory or supported archive still uses double-click or the application context menu's **Open** action; opening a regular archive or SFTP entry opens Quick View without extracting/downloading it to a local file.

Each tab has its own target, path, back/forward history, filter mode and filter text. Left and right tab sets are saved independently. A path that is unavailable at startup remains visible as an unavailable tab and is not silently redirected. Persisted paths are revalidated against their indexed target before browsing.

Non-ZIP archive locations are read-only. ZIP supports F5 add/copy and F8 deletion; F4, F6, F7 and rename remain unavailable inside every archive. All archive mutations respect Odyssey's global read-only/file-management mode and selected conflict policy. ZIP deletion has a separate confirmation and currently cannot be undone.
