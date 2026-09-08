# Privacy

FixtureVault reads configured fixture files locally to report governance findings. It does not upload fixture contents, matched values, file hashes, or reports.

After a successful completed scan, it uses KeelMatrix.Telemetry for one anonymous activation signal and at most one weekly heartbeat according to that package's documented opt-out and retention rules. Telemetry failures do not affect scanning. Set `KEELMATRIX_NO_TELEMETRY=1` to disable telemetry for the current process.
