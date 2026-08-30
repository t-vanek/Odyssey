# Odyssey product stability scenarios

This suite validates behavior visible to a user, not isolated implementation methods. Automated scenarios use normal files and directories plus the production SQLite/FTS, scan, content extraction, search, background automation, and file-operation services.

## Automated release gates

| ID | User situation | Required outcome |
|---|---|---|
| STAB-01 | The user indexes a document, remembers words from its contents, and restarts Odyssey. | The session, target, filename result, and content result survive fresh service instances; the source SHA-256 is unchanged. |
| STAB-02 | A catalogue contains 1,250 matching files. | Lazy pages are complete, non-overlapping, deterministic, and every active result still exists. |
| STAB-03 | Files are added, changed, and deleted while several searches run during a rescan. | No query or scan fails; the final index contains every known record and distinguishes 25 missing files from 515 active files. |
| STAB-04 | A previously indexed removable location temporarily disappears. | The scan reports the inaccessible path without converting known work into false deletions. |
| STAB-05 | The user copies, renames, and undoes a file operation, then returns to read-only mode. | The filesystem and search index agree after every step, undo affects the newest operation, and read-only mode blocks the next write. |
| STAB-06 | The user cancels a large verified copy after transfer begins. | The source SHA-256 is unchanged, no final or partial destination remains, and no completed operation is recorded. |
| STAB-07 | A catalogue contains Czech text, emoji, and a directory link pointing back into the target. | Unicode names are searchable, the link itself is visible, and the scanner never follows the loop. |
| STAB-08 | Two components request a scan of the same target concurrently. | The scans are serialized, both complete, and the index remains idempotent. |
| STAB-09 | Another connection briefly owns the SQLite write lock. | Search remains responsive, the write waits within the busy timeout, and commits after the lock is released. |
| STAB-10 | Odyssey restarts with a persisted transfer marked as running. | The job is safely queued again, completes as attempt two, verifies byte-for-byte, and leaves no transfer artifact. |
| STAB-11 | Copy or move replaces an indexed customer file and the user chooses Undo. | The original bytes, size, active index entry, and moved source are restored immediately. |
| STAB-12 | A watched folder receives 120 creates plus rapid renames and writes. | Events are debounced into bounded verification scans and the index converges without duplicate active paths. |
| STAB-13 | The user cancels a real scan after several batches have already reached the index. | Previously known work is never marked missing, the scan finishes as cancelled without hanging, and the next scan converges to all 400 active files. |
| STAB-14 | SQLite reaches its real page limit while 900 new files are being indexed. | The scan fails without escaping into the application, the earlier result remains active, and a scan after space returns reaches the complete duplicate-free state. |
| STAB-15 | Odyssey restarts after a process stops with a running scan and a durable partial checkpoint. | The old scan becomes interrupted exactly once, one linked verification scan completes, and all 300 files are active without duplicates. |
| STAB-16 | The persisted transfer queue contains malformed JSON or a semantically empty job. | Startup continues with an empty valid queue and preserves the invalid bytes in a uniquely named diagnostic quarantine file. |
| STAB-17 | The process stops after a complete newer `transfer-queue.json.tmp` is flushed but before publication. | Startup promotes the complete snapshot, resumes its running job as attempt two, and removes the temporary file. |
| STAB-18 | The process stops while writing a truncated temporary queue snapshot and an older published queue still exists. | The valid published jobs remain authoritative and the incomplete temporary bytes are quarantined for diagnosis. |
| STAB-19 | A process stops after Odyssey registered and partly wrote a destination artifact; another similarly named file is present but unregistered. | Restart deletes only the exact journal-owned artifact and leaves the unregistered file byte-for-byte intact. |
| STAB-20 | A manipulated artifact journal points at an ordinary user document instead of its identity-bound temporary path. | The journal is quarantined and the user document is never deleted or modified. |
| STAB-21 | Another Odyssey service instance starts while a journal-owned transfer is active in a live process. | The active artifact and its ownership record remain untouched; only artifacts whose owner process ended are recoverable. |
| STAB-22 | Two independent operation services copy and verify large files concurrently while sharing one application-data directory. | Both destinations verify, neither service loses the other's ownership record, no partial artifact remains, and the journal ends empty. |
| STAB-23 | A real two-target catalogue contains 760 files with controlled Unicode names, extensions, sizes, and timestamps. | Combined FTS/target/category/extension/size/date filters exactly match independent filesystem truth across pages, and catalogue ordering is complete and deterministic. |
| STAB-24 | The user remembers only phrases inside real PDF, DOCX, XLSX and PPTX files or a filename inside ZIP; another PDF is corrupt. | Every valid phrase finds the exact source through content evidence, the corrupt file records an isolated error but remains findable by name, and no source hash or timestamp changes. |
| STAB-25 | A document is saved after its extraction candidate is selected but before content reading begins. | No content is attached to stale metadata; Odyssey schedules a verification scan, extracts the new version, removes the obsolete phrase, and publishes matching size and timestamp. |
| STAB-26 | Odyssey stops after moving the original destination aside for Replace but before publishing the replacement. | On restart, the identity-bound backup is restored byte-for-byte, the partial artifact is removed, and the journal is closed only after the original is visible again. |
| STAB-27 | Odyssey stops after publishing a verified replacement but before closing its transfer journal. | On restart, the published destination remains unchanged and the byte-identical original becomes a normally visible `odyssey-recovered-original` item beside it instead of remaining hidden as an internal backup. |
| STAB-28 | A manipulated journal supplies an ordinary user file as the supposed Replace backup. | Strict transaction-derived path validation quarantines the whole journal; the destination, partial artifact, and unrelated user file are not moved, deleted, or modified. |
| STAB-29 | The user cancels a large Replace operation after copying has begun. | The source and original destination retain their exact hashes, the incomplete replacement and identity-bound backup are removed, the journal closes cleanly, and no undo entry is advertised for an operation that did not finish. |
| STAB-30 | Odyssey stops during Undo after the replacement has been moved away but before the original reaches its destination. | Restart recovery recognizes the original transaction identity, restores the original byte-for-byte, and closes the journal only after the destination is visible again. |
| STAB-31 | A Replace backup has disappeared before the user requests Undo. | Undo is refused before touching the current destination or source, remains available for a later recovery attempt, and creates no misleading recovery transaction. |
| STAB-32 | A user item already occupies the deterministic visible name selected for a recovered original. | Odyssey never overwrites it, chooses the next available name, preserves all three versions byte-for-byte, and then closes the recovery journal. |
| STAB-33 | A valid destination filename is close to the filesystem component-length limit. | Replace, verification and Undo succeed because journal-owned part and backup names have bounded GUID-only components rather than appending metadata to the user's long name. |

Run once:

```bash
dotnet test Odyssey.slnx --filter "Category=Stability"
```

CI and the release workflow run the full test suite and then repeat this category three times on both supported operating systems. A release must not retry or ignore a failed pass.

## Manual hardware acceptance before public beta

These cases require real hardware or desktop interaction and must not be simulated by changing production safety checks:

| ID | Setup and action | Pass condition |
|---|---|---|
| HW-01 | Index a removable NTFS/exFAT drive, unplug it while Odyssey is idle, reconnect it, and search again. | Odyssey remains responsive, preserves known results while absent, detects the return, and converges without duplicates. |
| HW-02 | Start a scan on a slow USB HDD, terminate the Odyssey process, and launch it again. | The UI calmly reports recovery, committed results remain searchable, and a verification scan completes without marking accessible files missing. |
| HW-03 | Suspend and resume Windows/Linux during background indexing. | No crash or permanent busy state; watchers and search recover automatically. |
| HW-04 | Copy a multi-gigabyte file in managed mode, cancel it, and inspect both locations. | The source hash is unchanged and no partial destination is presented as completed. |
| HW-05 | Scan folders containing Czech characters, emoji, very long paths, hidden files, links, and denied subfolders. | Accessible entries remain searchable with their exact paths; inaccessible entries create recoverable errors, not a failed application. |
| HW-06 | Fill the application-data volume until SQLite cannot grow. | Odyssey reports a calm actionable failure, does not corrupt the existing index, and opens it successfully after space is restored. |
| HW-07 | Install a signed release, update to a newer release, and deliberately supply a package with the wrong digest. | The valid update preserves settings and restarts; the altered package is rejected before replacement. |

For every manual run record the Odyssey version, OS build, filesystem, device model, approximate file count, elapsed time, peak memory, result, and relevant log. Any source-file hash change during a read-only scenario is a release blocker.
