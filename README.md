# File Storage Service

A C# / ASP.NET Core service for resumable file uploads and version history. PostgreSQL stores metadata; S3 stores chunks. A small CLI handles uploads, downloads, and checksum verification.

- Buzhash rolling hash splits files into content-defined chunks, so inserting bytes does not shift every later boundary.
- SHA-256 identifies chunks for deduplication within each user’s storage.
- Upload sessions track missing chunks and expire after 24 hours. Retrying completion returns the same version.
- PostgreSQL transactions and advisory locks serialize each user’s writes, including writes from multiple API instances.

## Run locally

Requires Docker Compose and the .NET 10 SDK. The first build compiles a pinned MinIO release from source and can take several minutes. No AWS account is needed.

```sh
docker compose up -d --build
export FILE_STORAGE_URL=http://localhost:8080
export FILE_STORAGE_TOKEN=local-development-token-change-me-12345
dotnet build src/FileStorage.Cli -c Release
dotnet src/FileStorage.Cli/bin/Release/net10.0/FileStorage.Cli.dll upload ./example.pdf
```

Use the returned IDs to download a version or upload a new one:

```sh
dotnet src/FileStorage.Cli/bin/Release/net10.0/FileStorage.Cli.dll download <file-id> <version-id> ./copy.pdf
dotnet src/FileStorage.Cli/bin/Release/net10.0/FileStorage.Cli.dll upload ./example.pdf --file-id <file-id>
dotnet src/FileStorage.Cli/bin/Release/net10.0/FileStorage.Cli.dll list
dotnet src/FileStorage.Cli/bin/Release/net10.0/FileStorage.Cli.dll versions <file-id>
```

Rerun an interrupted upload with the same arguments to resume it. Progress lives in `<path>.upload.json`; use `--state <path>` to choose another location. Downloads verify SHA-256 before moving into place and never overwrite an existing file.

## Configuration

Compose uses local development credentials and binds ports to localhost. Outside local development, use HTTPS and private database/object storage endpoints.

| Environment variable | Purpose |
| --- | --- |
| `ConnectionStrings__Metadata` | PostgreSQL connection string |
| `Storage__ApiKeys__<user>` | Distinct bearer token, at least 32 characters |
| `Storage__Bucket` | S3 bucket name |
| `Storage__Region` | AWS region; defaults to `us-east-1` |
| `Storage__Endpoint` | Custom S3 endpoint; omit for AWS |
| `Storage__CreateBucket` | Create a missing bucket; defaults to false |

For AWS, use the SDK credential chain with an IAM role or local profile. A pre-created bucket needs `s3:GetBucketLocation`, `s3:GetObject`, and `s3:PutObject` permissions. The integration tests use MinIO; an AWS account is not part of CI.

Files are limited to 512 MiB and 8,192 chunks; chunks are at most 1 MiB. The API allows eight concurrent storage requests per user. Versions and uploaded chunks are retained; deletion and automatic garbage collection are outside this version’s scope.

## Tests

```sh
dotnet test --project tests/FileStorage.Core.Tests
dotnet test --project tests/FileStorage.Cli.Tests
docker compose up -d --wait postgres minio
dotnet test --project tests/FileStorage.Api.Tests
```

CI also builds the API container and runs `python3 tests/acceptance.py` against it. Tests cover chunk reuse, resumability, concurrent completion, version history, user isolation, invalid input, and storage failures. See [the API contract](docs/api.md) for routes and request formats.
