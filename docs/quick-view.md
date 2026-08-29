# Quick View

Press `F3` on one local file in the active Commander panel. Quick View is read-only and is available in Odyssey's default read-only mode.

The viewer reads one bounded block at a time. Choose 16, 32, 64, or 256 KiB, then use `Page Up`/`Page Down` or the navigation buttons to move through the file. `Ctrl+Home` and `Ctrl+End` jump to the first or last block. Loading can be cancelled. `Escape` closes the viewer after an active read has finished or been cancelled.

**Automatic** display uses text for detected text and hex for binary data. Text encoding is detected from UTF-8/UTF-16 byte-order marks and bounded content heuristics. The encoding list can force UTF-8, UTF-16 LE/BE, ISO-8859-1, or ASCII. **Hex** works for every regular local file and shows an absolute byte offset, sixteen bytes, and printable ASCII on each line.

Every block is tied to the file length and modification timestamp captured when the viewer opened. Odyssey refuses another block if that snapshot changes and also checks after each read. The file and all of its ancestors must be regular filesystem paths, not symbolic links or reparse points. The viewer uses pooled bounded buffers and does not create temporary files.

Current limits are intentional: archive and SFTP entries are not preview sources yet. Syntax highlighting, images and EXIF, PDF rendering/text, media thumbnails/metadata, and rich document metadata are also not implemented. Use double-click or the context-menu **Open** action to open a file with the operating system, and to navigate into a supported archive.
