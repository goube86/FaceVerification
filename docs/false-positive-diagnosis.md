# SFace false-positive diagnosis

## Root cause

The project called the SFace ONNX model directly but did not reproduce the input contract of OpenCV's `FaceRecognizerSF::feature`. The previous path decoded an aligned BGR image and transformed every channel with `(value - 127.5) / 128`, leaving the channel order as BGR. The official wrapper creates its blob with scale `1`, mean `0`, and `swapRB=true`: SFace therefore receives raw RGB values in `[0,255]`.

This was not a threshold defect. Cosine similarity and the three-way threshold ordering were already conceptually correct, but cosine was being calculated over features produced from an out-of-contract tensor. Those features do not retain the model's calibrated identity geometry, so genuine-looking high scores for impostor pairs cannot be interpreted using SFace cosine thresholds.

## Audited flow and correction

1. The controller reads each multipart file into a different byte array.
2. Quality validation decodes each array independently.
3. YuNet receives each image independently and returns face count, box, confidence, and five landmarks.
4. Verification requires exactly one sufficiently large, centered face per image.
5. Each source image and its own landmarks are passed to alignment, producing separate 112×112 encoded crops.
6. SFace now receives RGB CHW values in raw `[0,255]` scale. Each 128-value model output is copied before the next inference, checked for expected dimension and finite non-zero norm, and L2-normalized.
7. The comparator rejects empty, mismatched, constant, non-finite, and zero-norm vectors. It calculates native cosine similarity and normalized L2 explicitly.
8. The decision uses the native cosine directly: `>= MatchThreshold` is `Match`, `<= NonMatchThreshold` is `NotMatch`, and the interval is `Inconclusive`.

The public `similarityScore` remains unchanged in meaning and shape: native cosine in `[-1,1]`, where larger means more similar. It is not converted with `1 - distance`, `(score + 1) / 2`, or any other display transform. L2 is diagnostic only.

Debug logs contain metric, native/exposed score, L2, norms, dimensions, face counts, confidence/coordinates, and aggregate stage timings. They never contain images, Base64, complete embeddings, or sensitive paths.

## Validation boundary

Automated tests cover deterministic comparison/decision rules, independent buffers and overwrite resistance, invalid embeddings, face-count rejection, preprocessing layout, and real ONNX embedding invariants. The repository has no approved biometric photographs, so genuine/impostor accuracy is validated through the ignored local fixture matrix described in [model evaluation](model-evaluation.md). Do not interpret the synthetic real-model tests as an accuracy benchmark.
