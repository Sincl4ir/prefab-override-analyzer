# Prefab Override Analyzer

Finds and removes no-op prefab overrides that bloat your prefab files — without touching real overrides.

> Requires [Odin Inspector](https://odininspector.com/) (paid asset) to compile and run.

## What it does

Unity prefab instances can accumulate "phantom" overrides — property modifications
that were saved but match the prefab's actual value, doing nothing except bloating
the `.prefab` file and cluttering diffs.

This tool scans every prefab instance under a target prefab, classifies each
override as **redundant**, **real**, or **unknown**, and reports the breakdown.
Optionally, it strips only the redundant ones and re-saves the prefab.

## Features

- Read-only analysis pass with a per-property breakdown
- Safe removal: only provably no-op overrides are stripped; anything ambiguous is kept
- Confirmation dialog before writing changes to disk
- Reports file size saved after cleanup

## Usage

1. `Tools > Prefab Override Analyzer`
2. Assign a prefab
3. **Run Analysis** to preview (no changes made)
4. **Revert Redundant Overrides** to strip the no-op overrides and save

## License

MIT for this code. Odin Inspector is a separate paid asset, not included.
//EOF.
