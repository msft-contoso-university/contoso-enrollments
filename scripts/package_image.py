"""Export an already-built image without rebuilding or approving it."""
import hashlib
import json
import os
import re
import shutil
import subprocess
import tarfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


def inspect_archive(path, image, sha):
    with tarfile.open(path, "r:") as archive:
        members = {}
        for member in archive:
            name = member.name.removeprefix("./")
            parts = PurePosixPath(name)
            if parts.is_absolute() or ".." in parts.parts or member.issym() or member.islnk():
                raise ValueError("Unsafe OCI archive entry")
            if member.isdir():
                continue
            if not member.isfile() or name in members or len(members) >= 1024:
                raise ValueError("Unexpected or duplicate OCI archive entry")
            members[name] = member

        def read_json(name):
            member = members.get(name)
            if member is None or member.size > 2 * 1024 * 1024:
                raise ValueError("Missing or oversized OCI metadata")
            with archive.extractfile(member) as stream:
                return json.load(stream)

        def verify_blob(descriptor):
            digest = descriptor.get("digest", "")
            if not DIGEST.fullmatch(digest):
                raise ValueError("An exact OCI SHA256 digest is required")
            name = "blobs/sha256/" + digest.removeprefix("sha256:")
            member = members.get(name)
            if member is None or member.size != descriptor.get("size"):
                raise ValueError("Missing or incorrectly sized OCI blob")
            with archive.extractfile(member) as stream:
                if "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest() != digest:
                    raise ValueError("OCI blob digest mismatch")
            return name

        if read_json("oci-layout") != {"imageLayoutVersion": "1.0.0"}:
            raise ValueError("Unsupported OCI layout")
        index = read_json("index.json")
        descriptors = index.get("manifests", [])
        if index.get("schemaVersion") != 2 or len(descriptors) != 1:
            raise ValueError("Exactly one candidate image is required")
        descriptor = descriptors[0]
        if descriptor.get("mediaType") != "application/vnd.oci.image.manifest.v1+json":
            raise ValueError("Expected an OCI image manifest")
        manifest = read_json(verify_blob(descriptor))
        if manifest.get("schemaVersion") != 2:
            raise ValueError("Unsupported OCI manifest")
        config_descriptor = manifest["config"]
        if config_descriptor["digest"] != image["Id"]:
            raise ValueError("OCI export changed the built image identity")
        config = read_json(verify_blob(config_descriptor))
        if config.get("os") != "linux" or config.get("architecture") != "amd64":
            raise ValueError("Expected the tested Linux AMD64 image")
        settings = config.get("config", {})
        if settings.get("Labels", {}).get("org.opencontainers.image.revision") != sha:
            raise ValueError("Image revision differs from checkout")
        user = settings.get("User", "").split(":")[0]
        if not user or user == "root" or (user.isdecimal() and int(user) == 0):
            raise ValueError("The runtime image must use a nonroot user")
        layers = manifest.get("layers", [])
        diff_ids = config.get("rootfs", {}).get("diff_ids")
        if not layers or len(layers) != len(diff_ids or []) or diff_ids != image["RootFS"]["Layers"]:
            raise ValueError("OCI export changed the tested image filesystem")
        for layer in layers:
            verify_blob(layer)
        return descriptor["digest"]


def main():
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if not re.fullmatch(r"[a-f0-9]{40}", sha):
        raise ValueError("An exact source commit is required")
    if "GITHUB_SHA" in os.environ and os.environ["GITHUB_SHA"] != sha:
        raise ValueError("Checkout SHA does not match workflow SHA")
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip():
        raise ValueError("Refusing to export an image from a dirty source tree")
    image_ref = f"enrollments-candidate:{sha}"
    images = json.loads(subprocess.check_output(["docker", "image", "inspect", image_ref], text=True))
    if len(images) != 1:
        raise ValueError("Exactly one built image is required")
    image = images[0]
    target = ROOT / "artifacts" / "candidate"
    target.mkdir(exist_ok=False)
    archive = target / f"contoso-enrollments-{sha}.oci.tar"
    subprocess.run(
        ["skopeo", "copy", "--format", "oci", f"docker-daemon:{image_ref}", f"oci-archive:{archive}:{sha}"],
        check=True,
    )
    manifest_digest = inspect_archive(archive, image, sha)
    (target / "sql").mkdir()
    shutil.copyfile(ROOT / "sql" / "migrations.sql", target / "sql" / "migrations.sql")
    files = sorted(path for path in target.rglob("*") if path.is_file())
    checksums = {}
    for path in files:
        with path.open("rb") as stream:
            checksums[path.relative_to(target).as_posix()] = hashlib.file_digest(stream, "sha256").hexdigest()
    metadata = {
        "status": "UNTRUSTED CANDIDATE - NOT AN APPROVED RELEASE",
        "service": "contoso-enrollments",
        "sourceCommit": sha,
        "entrypoint": "dotnet Contoso.Enrollments.Api.dll",
        "promotionOwner": "contoso-platform",
        "image": {
            "format": "oci-archive", "archive": archive.name, "manifestDigest": manifest_digest,
            "configDigest": image["Id"], "os": "linux", "architecture": "amd64",
        },
        "files": checksums,
    }
    (target / "candidate.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    (target / "SHA256SUMS").write_text(
        "".join(f"{digest}  {name}\n" for name, digest in checksums.items()), encoding="utf-8",
    )
    print(f"Untrusted candidate source {sha}; OCI manifest {manifest_digest}; config {image['Id']}")


if __name__ == "__main__":
    main()
