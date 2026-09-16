# FaceVerification

FaceVerification is a self-hosted ASP.NET Core Web API that validates two images and decides whether their single detected faces belong to the same person. It uses OpenCV YuNet for face detection, OpenCV SFace for embeddings, ONNX Runtime for local inference, and PostgreSQL for short-lived verification sessions and audit results.

## Scope and trust boundary

The API validates encoded image bytes, dimensions, sharpness, exposure, face count, face size, and position. It aligns five landmarks, creates normalized embeddings, compares cosine similarity, and never returns or persists images or embeddings.

Liveness is **not** performed by this backend. A web or mobile client is responsible for liveness. Optional `livenessTransactionId` and `livenessEvidence` values are accepted only as future integration material; only an evidence hash is stored and the API never claims that liveness was validated. A future signed-evidence verifier can be placed in the Application boundary without trusting a client boolean.

The configured match thresholds are initial engineering values, not production guarantees. They must be calibrated with a representative, consented dataset before production use.

## Technology stack

- .NET 10 LTS, ASP.NET Core controllers, native OpenAPI, and Scalar
- EF Core 10 and Npgsql with PostgreSQL 18.1
- ONNX Runtime 1.30
- OpenCvSharp/OpenCV 4.13
- Official OpenCV Zoo YuNet and SFace ONNX models
- xUnit

## Architecture

`Domain` contains session rules and persisted business records. `Application` defines model-agnostic contracts and use cases. `Infrastructure` owns EF Core, PostgreSQL, OpenCV, and ONNX Runtime. `Api` owns HTTP, authentication, Problem Details, rate limiting, timeouts, and OpenAPI. Dependencies point inward only:

```text
Api -> Application <- Infrastructure
          |                 |
          v                 v
        Domain <------------+
```

See [architecture](docs/architecture.md), [API reference](docs/api.md), and [model evaluation](docs/model-evaluation.md).

The SFace preprocessing contract and the false-positive root-cause audit are documented in [false-positive diagnosis](docs/false-positive-diagnosis.md).

## Prerequisites

Install the .NET 10 SDK, Docker Desktop or Docker Engine with Compose, PowerShell 7 (`pwsh`), and `just`. The API runs locally under `dotnet`; only PostgreSQL runs in Docker.

## Configuration

The first `just run` or `just migrate-database` creates `.env` from `.env.example` when it is missing and generates a cryptographically random development-only PostgreSQL password. The file is ignored by Git. You may instead copy `.env.example` to `.env` and set the values manually; never commit `.env`.

For local development, store the API connection string with User Secrets if it differs from `.env`:

```powershell
dotnet user-secrets init --project src/FaceVerification.Api
dotnet user-secrets set --project src/FaceVerification.Api "ConnectionStrings:FaceVerification" "Host=localhost;Port=5432;Database=face_verification;Username=face_verification;Password=YOUR_PASSWORD"
```

Production requires `Authentication:Authority` and `Authentication:Audience`. The local identity bypass can run only in `Development` and must be explicitly enabled. OpenAPI/Scalar exposure is also configurable and defaults to development only.

Important `Verification` settings include request/image limits, inference timeout and concurrency, minimum face size and quality, session lifetime, `MatchThreshold`, and `NonMatchThreshold`.

`Verification:BypassSessionValidation` temporarily bypasses verification-session and nonce checks only when the host environment is `Development`. It is enabled in `appsettings.Development.json` for the current development workflow and remains disabled by default. Bypassed requests are processed directly and are not persisted as session results. Production always requires a valid session and nonce regardless of this setting.

## Models and licenses

The repository contains the official OpenCV Zoo YuNet `2023mar` and SFace `2021dec` binaries, manifests, source URLs, licenses, and their real SHA-256 values. Startup verifies both hashes, creates one ONNX `InferenceSession` per model, and warms both sessions. A missing, modified, or invalid model prevents startup with a clear error. Models are never downloaded at runtime.

See [third-party notices](THIRD_PARTY_NOTICES.md).

## Database and migration policy

PostgreSQL data is retained in the named Docker volume `face-verification-postgres-data`; normal `just down` does not remove it. The schema has exactly one migration, `InitialCreate`.

During this initial stage, every schema change must be consolidated by regenerating `InitialCreate` and its synchronized model snapshot. Do not add repair migrations such as `AddMissingFields`.

## Local development commands

```bash
just migrate-database       # start PostgreSQL and apply InitialCreate
just run                    # start PostgreSQL, then run the API locally with visible logs
just down                   # stop only the script-owned API and this project's containers
just test                   # restore, build, and run only *.UnitTests.csproj
just recreate-database      # destructive; Development only and asks for the exact DB name
just recreate-database -Force
```

`just run` records its process in `.run/face-verification-api.pid`. It never applies migrations silently. `just down` validates the stored command line before stopping the API, will not stop an IDE-owned API, preserves the database volume, and never stops Docker Desktop or unrelated containers.

Without `just`, use `docker compose up -d postgres` and `dotnet run --project src/FaceVerification.Api`.

## HTTP surface

- Scalar: `http://localhost:<port>/scalar/v1`
- OpenAPI: `http://localhost:<port>/openapi/v1.json`
- Liveness: `/health/live`
- Readiness (PostgreSQL and both models): `/health/ready`
- `POST /api/v1/verification-sessions`
- `POST /api/v1/verification-sessions/{sessionId}/verify`

The verification operation uses `multipart/form-data` fields `referenceImage`, `capturedImage`, `nonce`, and optional `livenessTransactionId`/`livenessEvidence`. Scalar renders file selectors for both `IFormFile` fields.

Decisions are `Match`, `NotMatch`, `Inconclusive`, `InvalidReferenceImage`, `InvalidCapturedImage`, `FaceNotDetected`, `MultipleFacesDetected`, `InsufficientImageQuality`, `FaceTooSmall`, `UnsupportedImageFormat`, `CaptureSessionExpired`, `CaptureSessionAlreadyUsed`, and `ProcessingFailed`. `isMatch` is `null` for every decision other than `Match` and `NotMatch`.

Errors use RFC 9457-style Problem Details with stable `code`, `traceId`, and `correlationId` extensions. Send `X-Correlation-ID` to propagate your own correlation identifier.

## Testing

`just test` deliberately runs only unit tests and does not start PostgreSQL. Integration tests are in `FaceVerification.IntegrationTests`; set `FACE_VERIFICATION_TEST_CONNECTION` to an isolated database before running them. Model tests load and warm the real binaries and are separate because they are slower. No personal photographs are stored in this repository.

Run the complete automated suite, including the real-model invariants, with `dotnet test FaceVerification.slnx`. For the consented local A–D facial acceptance matrix and score export, follow [model evaluation](docs/model-evaluation.md). Local biometric images and generated score CSV files are ignored by Git.

Dependency review can be performed with `dotnet list FaceVerification.slnx package --vulnerable --include-transitive`; assess findings and update pinned versions deliberately.

## Security and privacy

- JWT Bearer delegates token issuance to the configured authority; there are no login/token endpoints.
- Nonces contain 256 bits of cryptographic randomness, are returned once, stored only as SHA-256, compared through an atomic conditional update, and expire with the session.
- Files are checked by signature and fully decoded; compressed size, dimensions, pixels, processing time, rate, and concurrency are bounded.
- Completed, failed, expired, or processing sessions cannot be replayed.
- Logs contain operational identifiers and aggregate outcomes, never images, embeddings, nonces, tokens, or complete evidence.
- Images and embeddings are not persisted. Establish jurisdiction-appropriate retention, consent, access, deletion, encryption, and incident-response policies before production.

## Troubleshooting

- **Startup hash error:** restore the exact official binary matching the manifest; never weaken checksum validation.
- **Readiness is unhealthy:** inspect `docker compose ps`, PostgreSQL credentials, and model paths/hashes.
- **Database missing:** run `just migrate-database`; `just run` intentionally does not migrate.
- **Authentication failure in development:** ensure `ASPNETCORE_ENVIRONMENT=Development` and `Authentication:EnableDevelopmentIdentity=true`. Never enable a bypass in production.
- **`pwsh` or `just` unavailable:** install them and confirm both are on `PATH`.
- **Threshold surprises:** read [model evaluation](docs/model-evaluation.md) and calibrate on representative data.

## Repository structure

```text
models/       Official binaries, manifests, and licenses
scripts/      PowerShell development orchestration
src/          Api, Application, Domain, Infrastructure
tests/        Unit, integration, and model test projects
docs/         Architecture, API, and evaluation guidance
```
