# Durable Contract Change Checklist

This is the normative v1 checklist for changes to FixtureVault policy, manifest, and report contracts. It applies to schema, rule, and exit-code changes that can affect existing repositories or consumers. Evaluate compatibility against current v1 behavior and representative prior-version documents; when a breaking change is required, increase the relevant version, document the impact and migration path, add the required evidence, and update the release notes before merging.

Use this checklist before changing a FixtureVault contract. Treat the current behavior and examples in the README as compatibility commitments.

## Contracts to Review

- `.fixturevault.json` policy file name, schema version, fields, types, defaults, and validation rules.
- `.fixturevault.manifest.json` file name, schema version, and `activeBaselines` path semantics.
- JSON report `schemaVersion`, fields, finding shape, skipped diagnostics, errors, and exit-code mapping.
- Stable rule IDs `FV001` through `FV008` and the meaning of each finding.
- Stable `FV-SKIP-*` skipped-diagnostic codes, including the per-file diagnostic recorded when content inspection cannot run: no supported encoding decodes the bytes, or the decoded text contains `U+0000` whatever the declared encoding. The reported reason states the observed condition and names the declared encoding when a byte-order mark declared one, and stable `FV-E0xx` error codes keep their exit-code mapping. `FV-E017` is the fail-closed resource/diagnostic-budget error; it never represents a silently truncated report.

## Compatibility Decision

- Prefer additive changes: optional fields with safe defaults, new skipped diagnostics, or new documented rule IDs that do not change existing meanings.
- Preserve existing names, types, defaults, path normalization, rule semantics, and exit codes when an additive change is sufficient.
- Increase the relevant schema version for a breaking or non-additive change, including removing or renaming a field, changing its type or requiredness, changing path or normalization semantics, changing the meaning of an existing rule ID, or changing exit-code behavior.
- Document the compatibility impact and migration path before merging.

## Evidence Required

- Add or update fixture and golden files for the new behavior.
- Add round-trip tests for policy, manifest, and report JSON where serialization is involved.
- Add compatibility tests that read representative prior-version documents and verify the intended result.
- Test console and JSON findings together when report behavior changes.
- For `FV007` generic-key changes, update the single alias/normalization definition shared by assignment parsing, decoded JSON property matching, and the fallback pattern. Cover `ApiKey`/`api_key`/`api-key`, `ClientSecret`/`client_secret`/`client-secret`, `Password`, `Pwd`, `Secret`, and `Token`, both assignment operators, unquoted and matching-quoted raw keys, rejection of leading-only/trailing-only/mismatched key quotes, sibling ordering, and Azure semicolon/comma/whitespace boundaries. Prove that parsed generic fields own only their exact key/operator/value spans: prefix text, unknown-key syntax, and text after unsupported separators must remain eligible for independent inspection, while classified marker values must not be re-scanned. For empty Azure values followed by whitespace, prove that any syntactically valid `name=value` starts a sibling field while only Azure credential keys are classified.
- Test human-readable escaping for control-character paths while asserting that JSON round-trips the actual path value.
- Test open-boundary replacement, regular-file checks, bounded growth and shrinkage, incremental entry limits, and the aggregate 4,096-record / 1 MiB JSON-field diagnostic budget across findings, skipped diagnostics, and collision diagnostics; incomplete scans must not activate successful-scan telemetry.
- Run the affected tests, full test project, format verification, and package inspection.

## Documentation and Release Notes

- Update the README's policy, manifest, report, rule, or exit-code sections.
- Update examples and troubleshooting guidance when user-visible behavior changes.
- Add one truthful Keep-a-Changelog entry under the appropriate section and mark breaking changes explicitly.
