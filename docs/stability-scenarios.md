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
