# Privacy

FixtureVault reads configured fixture files locally to report governance findings. It does not upload fixture contents, matched values, file hashes, or reports.

The shared telemetry source of truth is the [KeelMatrix.Telemetry README](https://github.com/KeelMatrix/Telemetry/blob/main/README.md) and its [PRIVACY.md](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md). Those documents define the shared package's payload, opt-out, local-storage, and 90-day retention contract.

After a successfully completed scan, FixtureVault uses `KeelMatrix.Telemetry` for one anonymous activation signal and at most one weekly heartbeat. Installation, `init`, configuration errors, and aborted scans do not activate telemetry. Telemetry failures do not affect scanning. Set `KEELMATRIX_NO_TELEMETRY=1` to disable telemetry for the current process.

FixtureVault never sends fixture names, file paths, root paths, repository names, rule IDs, findings, file counts, matched values, file contents, file hashes, or policy contents.
