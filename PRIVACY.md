# Privacy

FixtureVault reads configured fixture files locally to report governance findings. It does not upload fixture contents, matched values, file hashes, or reports.

The shared telemetry source of truth is the [KeelMatrix.Telemetry README](https://github.com/KeelMatrix/Telemetry/blob/main/README.md) and its [PRIVACY.md](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md). Those documents define the shared package's payload, opt-out, local-storage, and 90-day retention contract.

After a successfully completed scan, FixtureVault calls `TrackActivation()` and `TrackHeartbeat()` on `KeelMatrix.Telemetry`. The shared package owns opt-out parsing, identity and state, activation deduplication, heartbeat cadence, local queueing, delivery, and failure handling. Installation, `init`, configuration errors, and aborted scans do not call the telemetry client. Set `KEELMATRIX_NO_TELEMETRY=1` to disable telemetry for the current process.

An unrepresentable POSIX filename encountered during governed traversal produces an incomplete scan rather than a successful clean result. Ignored directories are pruned before descendant enumeration, so names behind an established ignored path are not decoded or reported and do not affect telemetry. The tool does not use replacement-decoded names for filesystem authorization, does not activate successful-scan telemetry for an incomplete result, and does not disclose filenames or fixture contents.

FixtureVault never sends fixture names, file paths, root paths, repository names, rule IDs, findings, file counts, matched values, file contents, file hashes, or policy contents.
