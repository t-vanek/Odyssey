# Directory synchronization modes

The current production implementation is preview-first additive synchronization only.

1. Open local directories in the left and right panels.
2. Run **Compare directories**.
3. Choose metadata comparison (type, size, modified time) or content comparison (SHA-256 for equal-size files).
4. Review and select differences.
5. Queue the selected direction, left→right or right→left.

The plan copies missing/changed selected content and can safely replace a reviewed destination according to the transfer conflict policy. Symbolic links are compared as links and are not traversed. The durable transfer queue provides progress, cancellation, pause/restart, retry, temporary publication, and optional verification.

Odyssey does not currently provide mirror or bidirectional synchronization, deletion propagation, last-successful snapshots, saved/scheduled profiles, or local↔SFTP synchronization. No comparison action deletes an unpaired item. These limitations are tracked in [the Commander roadmap](commander-roadmap.md).
