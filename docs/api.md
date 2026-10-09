# API

All `/v1` routes require `Authorization: Bearer <token>`. User names come from server configuration, never from a request body. Unknown resources and resources owned by another user both return 404.

| Method | Route | Result |
| --- | --- | --- |
| POST | `/v1/uploads` | Create a session; return its ID and missing chunk hashes |
| GET | `/v1/uploads/{id}` | Resume status, missing chunks, or completed version |
| PUT | `/v1/uploads/{id}/chunks/{sha256}` | Upload raw chunk bytes; safe to repeat while pending |
| POST | `/v1/uploads/{id}/complete` | Verify content and commit a version; safe to repeat |
| DELETE | `/v1/uploads/{id}` | Abort an unfinished session; safe to repeat |
| GET | `/v1/files?offset=0&limit=50` | Files ordered by most recent update |
| GET | `/v1/files/{id}/versions?before=2147483647&limit=50` | Versions newest first; use the last number as the next `before` |
| GET | `/v1/files/{id}/versions/{version}/content` | Stream a version with `X-Content-SHA256` and `Content-Length` |
| GET | `/health/live` | Process health; no authentication |
| GET | `/health/ready` | Database and object store connectivity; no authentication |

## Upload protocol

The CLI generates a manifest by running Buzhash over a 64-byte window, cutting after at least 64 KiB when the lower 18 bits are zero, or at 1 MiB. Chunk boundaries are independent of stream read sizes. SHA-256 is calculated for each chunk and the complete file. The server accepts any ordered chunk manifest within the limits, so other clients can choose their own chunking strategy.

Create a session with `Content-Type: application/json`:

```json
{
  "fileId": null,
  "name": "example.txt",
  "size": 3,
  "sha256": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
  "chunks": [
    {"hash": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "size": 3}
  ]
}
```

This manifest represents the bytes `abc`. Set `fileId` to an existing file ID to create its next version. Empty files use an empty chunk array and SHA-256 of the empty byte sequence.

An optional `Idempotency-Key` header makes session creation retryable. Keys are scoped to a user, contain 1–128 letters, digits, periods, underscores, colons, or hyphens, and cannot be reused for a different manifest. They remain reserved after completion or expiry.

Send each missing chunk using PUT, then POST completion with no body. Repeated hashes appear once in the missing list but keep all their positions in the manifest. Completion reads every chunk, checks its length and checksum, verifies the whole-file checksum, and commits metadata in one transaction. Only completed files appear in listings.

Pending sessions expire after 24 hours; create a fresh session to continue after expiry. Its missing list still benefits from chunks already stored for that user. Aborting a session does not delete chunks. Completed sessions remain readable and retryable after their original expiry time.

## Errors and limits

Errors have `error` and `message` fields. Invalid input returns 400, missing credentials 401, missing resources 404, incompatible state or incomplete uploads 409, expired pending sessions 410, oversized chunks 413, and storage failures 503. The concurrency limiter returns 429 when eight storage requests are already in progress for that user.

File names must be 1–200 characters with no control characters or path separators. File size is capped at 512 MiB, manifests at 8,192 entries, chunk size at 1 MiB, and request bodies at 2 MiB. All checksums use lowercase hexadecimal. List limits are 1–100; offsets cannot be negative.

Chunks live at `chunks/{user}/{sha256}` in S3. An S3 write followed by a failed database commit can leave an unreferenced object; retrying the PUT safely rewrites the same content. Metadata and object storage should be backed up together. If stored content is lost or corrupt, completion fails; resending the affected chunk to a pending session can repair it. A download that fails after streaming has begun is terminated, and the CLI discards its partial file.
