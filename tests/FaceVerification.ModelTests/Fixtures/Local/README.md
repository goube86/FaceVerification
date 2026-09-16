# Local biometric fixtures

This directory is for consented, access-controlled local acceptance fixtures. Image files are ignored by Git. Do not add personal photographs to the repository.

Use these exact names:

- `person-a-1.jpg`
- `person-a-2.jpg`
- `person-b.jpg`
- `person-c.jpg`
- `person-d.jpg`
- `no-face.jpg`
- `multiple-faces.jpg`

Start the API in the `Development` environment with the existing session bypass enabled, then run from the repository root:

```powershell
pwsh ./scripts/evaluate-faces.ps1 `
  -FixturesPath ./tests/FaceVerification.ModelTests/Fixtures/Local `
  -BaseUrl http://localhost:5276 `
  -OutputCsv ./face-verification-results.csv
```

The script uses `Guid.Empty`, records the native cosine score returned by the API, and fails when an actual decision differs from the expected matrix. Delete local fixtures and result CSV files according to the test-data retention policy after evaluation.
