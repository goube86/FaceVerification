# API

All business endpoints require Bearer authentication. The API reads the subject from `sub` and consuming application from `client_id`. Development may use the explicitly configured local identity only while the host environment is `Development`.

## Create a session

`POST /api/v1/verification-sessions` returns `201 Created` with `sessionId`, the one-time `nonce`, and `expiresAt`. The nonce cannot be recovered later.

## Verify a session

`POST /api/v1/verification-sessions/{sessionId}/verify` consumes `multipart/form-data`:

| Field | Required | Meaning |
|---|---:|---|
| `referenceImage` | yes | JPEG, PNG, or WebP reference image |
| `capturedImage` | yes | JPEG, PNG, or WebP client capture |
| `nonce` | yes | one-time session nonce |
| `livenessTransactionId` | no | opaque provider transaction reference, not validated |
| `livenessEvidence` | no | future provider evidence; only its SHA-256 is stored |

The default limit is 5 MiB per file, 4096×4096, and 16 million pixels. The response includes IDs, decision, nullable `isMatch`, similarity when available, applied thresholds, quality scores, model name/version, processing time, and UTC processing timestamp. It never contains an embedding or image.

For the current local workflow, `Verification:BypassSessionValidation` is enabled in `Development`. A caller may use any GUID in the `sessionId` route segment and omit `nonce`; the images are processed directly and no session result is persisted. This bypass is ignored outside `Development`, where a valid single-use session and nonce remain mandatory.

`isMatch` is `true` only for `Match`, `false` only for `NotMatch`, and `null` for `Inconclusive` and validation outcomes. Low quality is never represented as `NotMatch`.

## Errors

Malformed requests, authentication failures, consumed/expired sessions, rate limits, timeouts, and unexpected failures use Problem Details. Application-generated problems include stable `code`, `traceId`, and `correlationId`. `X-Correlation-ID` is echoed when supplied.

Common status codes are 400 (request), 401/403 (identity/authorization), 409 (session unavailable), 408 (timeout), 413 (request limit), 429 (rate limit), and 500 (unexpected processing failure).

Interactive development documentation is available at `/scalar/v1`, backed by `/openapi/v1.json`. Exposure is configurable.
