import hashlib
import json
import os
from pathlib import Path
import random
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

base = os.environ.get("FILE_STORAGE_URL", "http://localhost:8080").rstrip("/")
token = os.environ.get("FILE_STORAGE_TOKEN", "local-development-token-change-me-12345")
environment = dict(os.environ, FILE_STORAGE_URL=base, FILE_STORAGE_TOKEN=token)
cli = ["dotnet", "src/FileStorage.Cli/bin/Release/net10.0/FileStorage.Cli.dll"]


def command(*args, success=True):
    result = subprocess.run(cli + list(args), env=environment, capture_output=True, text=True, timeout=120)
    if success:
        assert result.returncode == 0, result.stderr
        return json.loads(result.stdout)
    assert result.returncode != 0, result.stdout
    return result.stderr


def request(route, method="GET", payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request(base + route, data=data, method=method, headers={
        "Authorization": "Bearer " + token, "Content-Type": "application/json"
    })
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.load(response)


for attempt in range(60):
    try:
        request("/health/ready")
        break
    except (urllib.error.URLError, TimeoutError):
        if attempt == 59:
            raise
        time.sleep(1)

with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    source = root / "sample.bin"
    data = random.Random(42).randbytes(3 * 1024 * 1024)
    source.write_bytes(data)
    first = command("upload", str(source))
    assert first["sha256"] == hashlib.sha256(data).hexdigest()
    assert first["number"] == 1
    destination = root / "first.bin"
    command("download", first["fileId"], first["id"], str(destination))
    assert destination.read_bytes() == data
    assert "already exists" in command("download", first["fileId"], first["id"], str(destination), success=False)

    source.write_bytes(data[:1000] + b"inserted bytes" + data[1000:])
    second = command("upload", str(source), "--file-id", first["fileId"])
    assert second["number"] == 2
    assert second["fileId"] == first["fileId"]
    restored = root / "second.bin"
    command("download", second["fileId"], second["id"], str(restored))
    assert restored.read_bytes() == source.read_bytes()
    old = root / "old.bin"
    command("download", first["fileId"], first["id"], str(old))
    assert old.read_bytes() == data
    assert [v["number"] for v in command("versions", first["fileId"])] == [2, 1]
    assert any(f["id"] == first["fileId"] and f["versions"] == 2 for f in command("list"))

    empty = root / "empty"
    empty.write_bytes(b"")
    empty_version = command("upload", str(empty))
    command("download", empty_version["fileId"], empty_version["id"], str(root / "empty-copy"))
    assert (root / "empty-copy").read_bytes() == b""

    resumable = root / "resume.bin"
    resumable.write_bytes(b"resume this file")
    digest = hashlib.sha256(resumable.read_bytes()).hexdigest()
    manifest = {"fileId": None, "name": resumable.name, "size": resumable.stat().st_size,
                "sha256": digest, "chunks": [{"hash": digest, "size": resumable.stat().st_size}]}
    upload = request("/v1/uploads", "POST", manifest)
    state_path = root / "resume-state.json"
    state_path.write_text(json.dumps({"server": base + "/", "request": manifest,
                                      "key": "acceptance-resume", "uploadId": upload["id"]}))
    resumed = command("upload", str(resumable), "--state", str(state_path))
    assert resumed["fileId"] == upload["fileId"]
    assert not state_path.exists()

    assert "Invalid command" in command("no-such-command", success=False)
    assert "Unknown option" in command("upload", str(source), "--unknown", "x", success=False)

print("CLI acceptance passed: upload, versions, download, empty files, resume, and invalid commands")
