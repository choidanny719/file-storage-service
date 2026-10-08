CREATE TABLE IF NOT EXISTS files (
    id uuid PRIMARY KEY,
    owner text NOT NULL,
    name text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS files_owner ON files(owner, id);
CREATE TABLE IF NOT EXISTS versions (
    id uuid PRIMARY KEY,
    file_id uuid NOT NULL REFERENCES files(id),
    number integer NOT NULL CHECK(number > 0),
    name text NOT NULL,
    size bigint NOT NULL CHECK(size >= 0),
    sha256 text NOT NULL CHECK(length(sha256) = 64),
    manifest jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(file_id, number)
);
CREATE TABLE IF NOT EXISTS chunks (
    owner text NOT NULL,
    hash text NOT NULL CHECK(length(hash) = 64),
    size integer NOT NULL CHECK(size BETWEEN 1 AND 1048576),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(owner, hash)
);
CREATE TABLE IF NOT EXISTS uploads (
    id uuid PRIMARY KEY,
    owner text NOT NULL,
    file_id uuid NOT NULL,
    request jsonb NOT NULL,
    request_hash text NOT NULL,
    idempotency_key text NOT NULL,
    state text NOT NULL DEFAULT 'pending' CHECK(state IN ('pending', 'completed', 'aborted')),
    version_id uuid REFERENCES versions(id),
    expires_at timestamptz NOT NULL DEFAULT (clock_timestamp() + interval '24 hours'),
    UNIQUE(owner, idempotency_key),
    CHECK((state = 'completed') = (version_id IS NOT NULL))
);
