# Architecture

## Layers and dependencies

- **Domain** has no project dependencies. It owns `VerificationSession`, legal state transitions, the decision enum, and persisted result facts.
- **Application** references only Domain. It owns input/output records, replaceable vision contracts, repositories, options, and session use cases.
- **Infrastructure** references Application and Domain. It implements PostgreSQL persistence, OpenCV validation/alignment, ONNX inference, cosine comparison, model integrity checks, health checks, and session maintenance.
- **Api** references Application and Infrastructure. It owns HTTP models, authentication, authorization, multipart binding, Problem Details, timeouts, rate limiting, correlation, OpenAPI, and Scalar.

Neither Domain nor Application references ASP.NET Core, EF Core, OpenCV, or ONNX Runtime. Replacing SFace requires a new `IFaceEmbeddingProvider` implementation and DI registration, with no endpoint, use-case, or domain change.

## Verification flow

1. An authenticated caller creates a session. A 256-bit nonce is returned once; only SHA-256 is stored.
2. The multipart request supplies the session ID and nonce. PostgreSQL atomically changes the row from `Pending` to `Processing` only when ID, unexpired time, status, and nonce hash match.
3. Each file is bounded, checked by real signature, fully decoded with orientation handling, and checked for pixels, dimensions, sharpness, and exposure.
4. YuNet detects faces. Exactly one sufficiently large and centered face is required in each image.
5. Five landmarks produce a 112×112 affine alignment. SFace preprocessing mirrors `FaceRecognizerSF::feature`: decoded BGR pixels are reordered to RGB CHW and passed in their raw `[0,255]` scale. The ONNX graph performs the model's own scaling; applying `(value - 127.5) / 128` before inference is incorrect for this model.
6. Each SFace result is copied into an independent 128-value array, validated, L2-normalized, and compared with cosine similarity (`FR_COSINE` semantics). The native cosine in `[-1,1]` is used directly for both the decision and the public `similarityScore`; larger means more similar and there is no `1 - distance` or `(score + 1) / 2` transformation. The interval between the configured thresholds is `Inconclusive`. Normalized L2 is calculated only for debug diagnostics.
7. Only decision metadata, scores, thresholds, model identity, timing, and tracing IDs are stored. The session becomes terminal.

Singleton model runtime ownership guarantees one loaded and warmed `InferenceSession` per model. A semaphore bounds concurrent inference and a linked cancellation token enforces timeout.

## Operational lifecycle

The maintenance worker expires pending sessions and fails abandoned processing sessions. PostgreSQL optimistic concurrency uses its row-version mechanism, while nonce consumption uses an atomic conditional update. Health checks separate process liveness from readiness dependencies.
