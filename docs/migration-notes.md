# Migration notes

## Commander workspace preferences

The preferences file remains backward compatible. Existing `ReadOnlyMode` and `AutomaticDrives` values are retained; new optional fields store left/right tab workspaces and the favorite-directory hotlist. Missing fields deserialize to their safe defaults.

Writes now use a temporary sibling file followed by replacement, so an interrupted preferences update does not intentionally overwrite the last complete JSON document. No credential fields were added. Persisted tab and hotlist paths are treated as untrusted input and are canonicalized/revalidated against the currently indexed targets before navigation.

Archive tabs add optional `ArchivePath` and `ArchiveDirectory` fields to the existing tab snapshot. Older preferences omit them and continue to deserialize as local tabs. Restored archive paths are revalidated for existence, supported format and indexed-target containment before any archive is read. No credentials or archive contents are stored in preferences.

Plain TAR support adds no persisted settings or queue schema. Existing ZIP recovery manifests remain compatible because their transaction-derived temporary paths are unchanged; the same schema now accepts strictly validated `.tar` destinations and performs format-aware recovery. TAR creation is a new capability only and does not reinterpret an existing archive as writable.
