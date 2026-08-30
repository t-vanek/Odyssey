# Reproducible Odyssey benchmark

Odyssey includes a deterministic end-to-end benchmark instead of relying on isolated microbenchmarks or marketing claims. It exercises the production filesystem scanner, SQLite store, FTS5 search service, content extractors for plain text, PDF, DOCX, XLSX, PPTX and ZIP, the guarded copy service, and SHA-256 verification.

The benchmark reports:

- corpus creation throughput;
- metadata scan throughput and memory snapshots;
- content-indexing throughput, processed counts and extraction errors by file format;
- per-query true positives, false positives, false negatives, precision and recall;
- raw latency samples plus median and p95 end-to-end search latency, including all result pages;
- verified local-copy throughput;
- operating system, runtime, architecture, logical processors and available-memory profile.

## Standard run

Run a Release build outside the debugger and close unrelated disk-intensive applications:

```bash
dotnet run --project benchmarks/Odyssey.Benchmarks -c Release -- \
  --files 100000 \
  --rich-documents 100 \
  --iterations 10 \
  --transfer-mib 1024 \
  --output artifacts/benchmarks/local-100k.json
```

The default run uses 5,000 plain-text files, 25 rich documents distributed evenly across PDF, DOCX, XLSX, PPTX and ZIP, five iterations per query and a 64 MiB verified copy. Exit code `0` means the known-result corpus retained perfect precision and recall, every expected file was scanned and content-indexed without error, and the transfer passed SHA-256 verification. Performance values are observations, not pass/fail thresholds.

`--files` controls the plain-text corpus. `--rich-documents` controls the total number of rich files, assigned round-robin to the five formats; use a multiple of five for a balanced profile. Set it to `0` when measuring only the large plain-text path.

For a fast correctness smoke run:

```bash
dotnet run --project benchmarks/Odyssey.Benchmarks -c Release -- \
  --files 500 --rich-documents 10 --iterations 2 --transfer-mib 4 \
  --output artifacts/benchmarks/smoke.json
```

CI executes this smoke profile on both Linux and Windows and retains each JSON report with the test artifacts. `ODYSSEY_BENCHMARK_COMMIT` or `GITHUB_SHA` binds a report to a source revision.

## Comparing another tool

Preserve the isolated corpus and its truth manifest:

```bash
dotnet run --project benchmarks/Odyssey.Benchmarks -c Release -- \
  --files 100000 --rich-documents 100 --iterations 10 --transfer-mib 1024 \
  --workspace artifacts/benchmark-workspaces \
  --keep-workspace \
  --output artifacts/benchmarks/odyssey.json
```

The console and JSON report contain `preservedWorkspacePath`. That directory contains:

- `corpus/` — the exact files to index or search with the comparison tool;
- `benchmark-corpus-manifest.json` — each query and every expected relative result path;
- `application-data/` — Odyssey's isolated index, excluded from the corpus.

Use the same machine, filesystem, power mode and corpus for every tool. Record cold and warm runs separately, restart between cold runs, use at least five repetitions, and report the median plus p95 rather than the best value. A comparison is valid only when its result set is evaluated against the manifest; a fast query with missing or extra results is not equivalent.

Do not compare Odyssey's verified-copy result with an unverified copy from another tool. Either enable equivalent post-copy verification there or report transfer-only and verification-inclusive measurements as separate columns.

## Interpretation limits

The generated corpus is controlled and repeatable, but it is not a substitute for field data. It measures accessible regular files plus deliberately small, valid PDF, DOCX, XLSX, PPTX and ZIP samples. The JSON keeps per-format counts and extraction errors, while named truth queries expose precision and recall for every rich format.

Large or malformed documents, encrypted archives, OCR, removable drives, network storage and heavily fragmented disks require separate profiles. Mixing those workloads into one score would hide the source of regressions. Treat corpus-generation time separately from scan and extraction time, and do not compare a warm operating-system cache with a cold run.

Never publish a general “faster than” or “more accurate than” claim from one machine, one run, or different corpora. Keep raw JSON reports so later releases can be compared using the same methodology.
