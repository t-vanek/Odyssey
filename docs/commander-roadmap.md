# Odyssey Commander modernization roadmap

Last audited: 2026-08-29. This file describes the checked-out working tree, including the pre-existing uncommitted work. It is an evidence-based implementation ledger, not a product promise.

Status meanings:

- **Done** — usable implementation is wired through the production UI/service and covered by automated tests.
- **Partial** — useful production behavior exists, but one or more stated acceptance criteria are missing.
- **Not started** — no production implementation was found. A button, label, metadata extractor, or third-party capability alone does not count.

## Baseline and current stage

- Baseline before this modernization stage: `dotnet build Odyssey.slnx` succeeded with 0 warnings/errors; `dotnet test Odyssey.slnx --no-build` passed 90/90 tests.
- Stage K1 — active-pane keyboard/filter/selection workflow: **Done**.
  - F2 and F3–F8 are routed from the window to commands that resolve selection only from `ActivePane`; F4 invokes the OS edit association (with a documented fallback on Windows).
  - Per-pane contains/glob/regex quick filters are validated and applied to the complete cached directory snapshot before bounded paging. Regex evaluation has a timeout. Invalid patterns do not replace the last valid view.
  - Select all, invert, same extension, glob/regex mask, and previous-selection restore are available in the context menu and through shortcuts.
  - Czech and English UI strings and active-pane/filter/selection tests are present.
  - Stage verification: full suite 95/95; stability category 5/5 in three consecutive runs; build 0 warnings/errors; NuGet vulnerability audit reported no vulnerable direct or transitive package; `git diff --check` passed.
- Stage K2 — independent persistent tabs and hotlist: **Done**.
  - Each left/right tab independently stores target, path, back/forward history, quick-filter state and case mode. New, duplicate, close and reopen-last-closed operations are wired to UI and active-pane shortcuts.
  - Left/right workspaces and favorite directories are stored atomically in backward-compatible preferences. Startup revalidates every tab against its indexed target; unavailable or out-of-bound persisted paths remain visible but are never browsed.
  - The hotlist supports adding an arbitrary existing directory inside indexed targets, opening it in the active panel, and removing it. Paths outside target boundaries are rejected.
  - Stage verification: full suite 101/101; targeted tab/filter/startup/preferences scenarios passed; stability category 5/5 in three consecutive runs; build 0 warnings/errors; NuGet vulnerability audit found no vulnerable direct or transitive package; `git diff --check` passed.
- Stage A1 — read-only archive filesystem and secure extraction foundation: **Done**.
  - Supported archives open in ordinary panel tabs with navigation, per-tab history/persistence, bounded paging and quick filters. ZIP is managed and always available; 7z, TAR, TAR.GZ, GZip, BZip2, XZ and RAR are capability-advertised only when the native 7-Zip engine initializes.
  - F5 extraction of files or trees uses the shared durable queue, access-mode gate, progress/cancellation/retry/restart state and destination conflict policy. Output is streamed into destination-side staging before publication; no archive mutation capability is exposed.
  - Entry paths, source/destination link boundaries, duplicate/case-colliding names, expanded quotas, compressed ratio (ZIP), free-space preflight, encrypted entries and declared output length are guarded. Hostile paths, links, quota excess, corrupt ZIP, cancellation cleanup, conflict preservation, provider capabilities and queue restart have automated tests.
  - A filter-load race found by the new archive-tab test was fixed by committing a page under the same generation lock used to invalidate the previous load.
  - Stage verification: full suite 116/116 in the primary run and three consecutive repeat runs; build 0 warnings/errors; NuGet vulnerability audit found no vulnerable direct or transitive package; `git diff --check` passed.
- Stage A2a — transactional ZIP writing and cross-archive queue path: **Done**.
  - A preview/confirmation toolbar workflow creates one ZIP from the complete local selection. F5 queues local→ZIP and archive→archive copies; F8 deletes reviewed ZIP entries with a separate permanent-change warning.
  - ZIP create/update/delete capabilities are real and format-specific. Other archive formats remain read-only. Every mutation performs a complete managed rewrite into a sibling ZIP, rescans it with the hostile-archive policy and only then replaces the original; inputs and their ancestors cannot cross link/reparse boundaries.
  - Archive→archive uses a controlled application-data relay because ZIP has no server-side transfer. Queue cancellation and failures clean the relay in-process.
  - Automated coverage includes multi-source creation, directory structure/empty folders, case/name collisions, Fail/KeepBoth/Replace, file-versus-directory conflicts, deletion, source and ancestor links, cancellation with byte-identical original, active/passive panel routing, durable local→ZIP and archive→archive relay cleanup.
  - Stage verification: full suite 127/127 in the primary run and three consecutive repeat runs; Debug and Release builds 0 warnings/errors; NuGet vulnerability audit found no vulnerable direct or transitive package; `git diff --check` passed.
- Stage A2b — crash-safe ZIP publication and relay reconciliation: **Done**.
  - Each ZIP publish has a durable, versioned application-data manifest written before the original is moved. Startup recovery runs before the transfer worker and is repeated before mutations.
  - Recovery validates absolute canonical ZIP paths, transaction-derived sibling names, regular-file/link boundaries, manifest size/count and archive integrity before completing publication or restoring a backup. Malformed or ambiguous state is preserved for manual inspection.
  - Startup deletes only exact GUID-named controlled relay directories and never follows nested links. Injected post-backup failure, pre-publish crash, published-target loss, corrupt replacement, forged artifact path and stale relay/link cases are automated.
  - Stage verification: archive scenarios 29/29; full suite 133/133 in three consecutive repeat runs; stability scenarios 5/5 in three consecutive runs; Debug and Release builds 0 warnings/errors; NuGet vulnerability audit found no vulnerable direct or transitive package; `git diff --check` passed.
- Stage N1 — verified native dependency packaging: **Done**.
  - CI builds the Linux x64 7-Zip 26.02 shared library from an exact upstream commit and runs native archive tests through `ODYSSEY_7ZIP_LIBRARY`. Release packaging verifies the SharpSevenZip-supplied Windows x64 DLL by SHA-256, removes wrong-platform payloads, and includes the correct native library, both license texts, third-party notices, and an internal asset manifest.
  - Finished ZIP/TAR.GZ release containers are extracted and their declared native hashes are independently checked before publication. Runtime selection preflights both loading and the expected `CreateObject` export, then safely falls back to another compatible candidate if necessary; it never downloads code.
  - Stage verification on Linux: Debug and release builds 0 warnings/errors; full suite 134/134 with the freshly built native engine; stability scenarios 5/5 in three consecutive runs; local staging and final-container verification passed for both RIDs, including a complete self-contained Linux package; NuGet audit found no vulnerable direct or transitive package; `actionlint` and `git diff --check` passed. The hosted Windows job and a tagged GitHub Release were not executed locally.
- Next production stage: writable TAR creation with transactional replacement and reproducible fixtures. Writable 7z remains gated on a supported writer and rollback coverage; do not advertise either capability early.

## 1. Keyboard-first Commander UX

| Capability | Status | Evidence / remaining work |
|---|---|---|
| Independent left/right tabs; open/close/duplicate/reopen | Done | Per-panel tab collections and active-pane shortcuts/UI; last tab cannot be closed. |
| Tab persistence | Done | Target/path/history/filter state is atomically persisted per panel and restored with boundary validation. |
| Favorites/editable hotlist | Done | Add/open/remove persistent directories inside indexed target boundaries. |
| Per-panel directory history | Done | Back/forward stacks are independent for every tab and survive restart. |
| Quick filter while typing | Done | Per-pane contains/glob/regex filter, case option, invalid-pattern state, Ctrl+F/Escape, snapshot-level paging. |
| Glob/regex filters and selection masks | Done | Shared timeout-bounded matcher; semicolon glob alternatives; context menu and mask prompt. |
| Select all/invert/by extension/restore previous | Done | Active-pane commands and visual ListBox synchronization. |
| Range selection/stable multiselect | Partial | Avalonia range/multiselect is enabled and selection survives valid filter refresh for loaded matches; cross-tab persistence and explicit keyboard range acceptance tests remain. |
| F3–F8 workflow | Partial | Keys are wired: F3 opens, F4 edits via OS association, F5/F6 use transfer flow, F7 creates, F8 trashes. Quick View is not yet implemented, so F3 is open rather than Quick View. |
| Configurable shortcuts/conflict detection | Not started | Current shortcuts are fixed. |
| Configurable button bar | Not started | Existing bar is fixed. |
| Safe panel command line | Not started | No command line exists. |
| Safe external-program argument passing | Partial | Single-path OS open/edit uses `ProcessStartInfo` without a shell command string; configurable multi-file external tools are missing. |
| Drag and drop | Not started | No internal or OS DnD workflow found. |
| System/application context menus | Partial | Application context menu exists; stable native system menu integration is missing. |
| Accessibility | Partial | Keyboard focus and several automation names exist; full screen-reader labeling, focus traversal audit, and platform accessibility testing remain. |
| Active-panel safety | Done | File commands resolve `ActivePane`; inactive selection regression test is present. |

## 2. Archives as virtual filesystem

Overall: **Partial**. Browsing/extraction plus crash-recoverable transactional ZIP create/add/replace/delete are production features. Non-ZIP writing, archive mutation Undo and entry preview are not.

| Capability | Status | Evidence / remaining work |
|---|---|---|
| Open archives in panel; navigation/tabs/history/filter | Done | Archive provider is wired into both panes; archive location survives tab/workspace restore only after target-boundary and availability validation. |
| ZIP browse/extract | Done | Managed streaming implementation with hostile/corrupt archive tests. |
| 7z/TAR/TAR.GZ/GZip/BZip2/XZ/RAR browse/extract | Partial | Releases now bundle a verified RID-specific 7-Zip engine and Linux CI executes a real 7z create/read scenario against its pinned source build. A complete hostile/corrupt fixture matrix for every listed non-ZIP format is still missing. |
| Read/write capability flags | Done | ZIP advertises tested create/update/delete; other formats advertise browse/extract only when available. |
| Secure file/tree extraction | Done | Canonical path checks, link rejection, quotas, free-space preflight, guarded streaming, cancellation cleanup and destination staging/publish are implemented. |
| Shared progress/cancel/retry/restart queue | Done | Archive→local jobs use the durable transfer queue; restart-during-extraction test passes. Byte-range resume within a compressed entry is not claimed. |
| ZIP/7z/TAR creation | Partial | Multi-selection ZIP creation is wired and tested. 7z and TAR creation are not implemented. |
| Add/replace/delete entries | Partial | ZIP supports Fail/Skip/KeepBoth/Replace and transactional tree deletion. Other formats are read-only. |
| Local→archive and archive→archive | Partial | Local→ZIP and controlled archive→ZIP relay use the durable queue. Non-ZIP destinations are capability-rejected. |
| Transactional archive rewrite/original preservation | Partial | ZIP writes a validated sibling, journals publication, restores caught failures and reconciles crash states at startup. Injected post-backup rollback and hostile recovery manifests pass. Mutation Undo and extraction-staging recovery remain missing. |
| Archive-entry Quick View/bounded cache | Not started | F3 reports the limitation; users can extract with F5. |

Remaining archive safety gates include mutation Undo (or an explicit user recovery workflow), extraction-stage crash cleanup, writable 7z/TAR fixture coverage, and proof that metadata not understood by each future writer is not silently weakened. Managed ZIP rewrites deliberately reject links and use store mode to remain within Odyssey's own compression-ratio policy.

## 3. Multi-Rename

Overall: **Not started**. Single-item safe rename and undo exist. There is no preview model/tool UI, transforms, metadata tokens, manual result editing, sorting/counters, plan import/export, filesystem/case collision analysis, two-phase batch executor, or whole-batch undo. A future stage must separate immutable preview from execution and test cycles (`a→b`, `b→a`), case-only renames, reserved names, injected plan changes, rollback, and read-only enforcement.

## 4. Quick View, editing, and content comparison

| Capability | Status | Evidence / remaining work |
|---|---|---|
| F4 local edit | Partial | OS edit association is invoked safely for one local file; built-in editor and explicit save/publish flow are missing. |
| Chunked text, encoding, syntax, hex, image/EXIF, PDF, media/document preview | Not started | Index extractors are bounded but are not a Quick View UI. |
| Archive/SFTP preview and bounded temp cache | Not started | No preview materialization/cache lifecycle. |
| Text side-by-side/inline diff and options | Not started | Directory comparison does not compare/display text hunks. |
| Binary/hex diff | Not started | No implementation. |
| Three-way merge/conflict markers/safe save | Not started | No implementation. |

## 5. Full directory synchronization

Overall: **Partial**. Recursive local↔local comparison supports metadata or SHA-256 content checks, does not traverse symbolic links, and produces a reviewed additive left→right/right→left copy plan submitted to the durable queue. Identical rows can be hidden.

Missing: one-way update policy distinctions, mirror, bidirectional state, deletion propagation and separate confirmation, last-successful snapshots, new/changed/deleted/conflict classification, time/size/hash/priority rules, include/exclude filters, explicit empty-directory preservation, local↔SFTP planning, profiles, scheduler, durable sync execution state, space/volume estimates, audit/restore manifests, restart tests, mirror deletion safety tests, and disconnect/low-space handling. Deletion remains off because no deletion sync mode exists.

## 6. Professional remote management

| Capability | Status | Evidence / remaining work |
|---|---|---|
| SFTP password + strict host fingerprint | Done | Session-only password, first-connect fingerprint rejection, exact expected fingerprint verification, no password in queue/settings. |
| Keys, encrypted keys, SSH agent, keyboard-interactive | Not started | Current request requires a password. |
| Secure credential storage/explicit save | Not started | No credentials are persisted, which is safe but not a storage feature. |
| Known-host lifecycle/key-change UX | Partial | Exact fingerprint is required; managed known-host records and explicit changed-key workflow are missing. |
| Reconnect/backoff/keepalive/timeouts | Not started | No resilient session policy. |
| True upload/download resume and changed-endpoint checks | Not started | Durable jobs restart, but byte-range resume is not implemented. |
| Bandwidth/parallelism/proxy/jump host | Not started | No implementation. |
| Remote mutations | Not started | Browser and transfer only; no remote rename/create/trash/delete UI. |
| Remote compare/sync | Not started | No implementation. |
| FTP/FTPS, WebDAV, SMB, S3 | Not started | No providers or dependencies. |
| Unified capability/queue semantics | Partial | Local/SFTP share endpoint records and queue behavior, but capability contracts cover browse only and retry classification is not provider-specific. |
| Remote→remote | Not started | No server-side or controlled relay transfer. |
| Network-independent integration tests | Partial | SFTP safety has service tests/test doubles, but full reproducible protocol-server coverage remains. |

Credentials must never be added to `FileTransferRequest`, queue JSON, ordinary preferences, logs, or audit. Certificate bypass-all controls are prohibited.

## 7. Advanced file tools

Overall: **Not started** as user-facing tools. SHA-256 is used internally for verified transfers/content comparison and xxHash64 for duplicate candidates, but there is no checksum UI/API, SHA-512/CRC32 manifest support, split/join, timestamp/attribute editor, link tools, hardlink count, sparse awareness, metadata-preserving transfer mode, POSIX permission view, Windows ACL/reparse view, or filesystem capability presentation. Do not label internal hashing as a checksum tool.

Secure erase remains intentionally absent. It must not be offered where physical overwrite cannot be guaranteed (SSD, copy-on-write, snapshots, or remote storage).

## 8. Operating-system integration

| Capability | Status | Evidence / remaining work |
|---|---|---|
| Open/system manager | Done | Explicit OS open and containing-folder operations exist. |
| Terminal/Open with/app associations | Partial | OS association open/edit exists; terminal action, safe selectable Open with, and app-managed associations are missing. |
| Native context menu | Not started | Application menu only. |
| File clipboard copy/cut/paste | Not started | Text path copy is not a file clipboard implementation. |
| Drag and drop | Not started | No implementation. |
| UNC/long paths | Partial | .NET path APIs are used; Windows-specific UI/acceptance tests are missing. |
| UAC helper | Not started | No helper exists; do not add a generic elevated runner. |
| Linux Trash specification | Partial | Trash is implemented through platform paths/commands in the existing safe operation service; conformance and restore metadata need dedicated review/tests. |
| Mount/unmount/eject | Partial | Discovery plus supported unmount/eject actions exist; mounting and capability-rich volume UI are missing. |
| Case sensitivity/free space/filesystem capabilities | Partial | OS-sensitive comparers and free-space display exist; per-volume case/capability detection is missing. |
| Disconnect during operation | Partial | cancellation/failure paths exist; dedicated disk/server disconnect recovery tests remain. |

## 9. Odyssey-specific lead

| Capability | Status | Evidence / remaining work |
|---|---|---|
| Natural local search across name/content/OCR/date/location | Partial | Name/path/content/OCR and structured date/location filters exist; full natural-language parsing is missing. |
| Explain why found | Done | Match evidence, snippets, terms, confidence band, and localized explanation are shown. |
| Known move/rename history / “where did it go” | Partial | Operation/audit records exist, but no dedicated lineage query/UI combines audit and index. |
| Cleanup suggestions/space estimate | Not started | Duplicate groups exist; no explainable cleanup planner or reclaim estimate UI. |
| Exact duplicates/similar photos | Partial | Exact byte-verified duplicates exist; perceptual photo similarity is missing. |
| Preview-first AI batch planning | Partial | Typed guarded plans, immutable approval, revalidation and audit exist for basic mutations; Commander batch tools are not integrated yet. |
| AI cannot self-approve | Done | Approval is human-only and execution revalidates immutable plan hash and preconditions. |
| Local processing/cloud opt-in | Partial | Current indexing/OCR is local and no file content upload was found; an explicit centralized future cloud-content opt-in policy is not yet modeled. |

## Cross-cutting release gates

- Read-only enforcement, canonical boundary validation, link/reparse safety, streaming/cancellation, atomic publication, credential redaction, capability enforcement, localization parity, and bounded UI lists are mandatory for every stage.
- Tests still specifically missing from the target acceptance list: writable 7z/TAR fixtures; batch rename cycles/collisions/rollback; sync restart/mirror deletion; remote resume with changed endpoint; injected low-space and disk/server disconnect; credential-persistence scan across every future provider; very-large preview memory; and capability enforcement for future writable/remote providers. ZIP traversal, source/destination links, case/name collisions, quota, corruption, cancellation, conflict preservation, create/add/delete, archive capability, active-pane routing, queue restart, post-backup rollback, crash recovery, forged recovery paths and controlled relay/link cleanup now pass.
- Final verification is not yet claimable. At the end of every completed stage run relevant tests and `dotnet build --no-restore`; before a release run the complete suite, repeated stability scenarios, vulnerability audit, `git diff --check`, and available Windows/Linux builds.

## Explicit exclusions

No plugin system, plugin SDK, Total Commander plugin compatibility, or extension marketplace will be introduced.
