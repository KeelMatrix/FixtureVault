# Durable Contract Change Checklist

Use this checklist before changing a FixtureVault contract. Treat the current behavior and examples in the README as compatibility commitments.

## Contracts to review

- `.fixturevault.json` policy file name, schema version, fields, types, defaults, and validation rules.
- `.fixturevault.manifest.json` file name, schema version, and `activeBaselines` path semantics.
- JSON report `schemaVersion`, fields, finding shape, skipped diagnostics, errors, and exit-code mapping.
- Stable rule IDs `FV001` through `FV008` and the meaning of each finding.

## Compatibility decision

- Prefer additive changes: optional fields with safe defaults, new skipped diagnostics, or new documented rule IDs that do not change existing meanings.
- Preserve existing names, types, defaults, path normalization, rule semantics, and exit codes when an additive change is sufficient.
- Increase the relevant schema version for a breaking or non-additive change, including removing or renaming a field, changing its type or requiredness, changing path or normalization semantics, changing the meaning of an existing rule ID, or changing exit-code behavior.
- Document the compatibility impact and migration path before merging.

## Evidence required

- Add or update fixture and golden files for the new behavior.
- Add round-trip tests for policy, manifest, and report JSON where serialization is involved.
- Add compatibility tests that read representative prior-version documents and verify the intended result.
- Test console and JSON findings together when report behavior changes.
- Run the affected tests, full test project, format verification, and package inspection.

## Documentation and release notes

- Update the README's policy, manifest, report, rule, or exit-code sections.
- Update examples and troubleshooting guidance when user-visible behavior changes.
- Add one truthful Keep-a-Changelog entry under the appropriate section and mark breaking changes explicitly.
