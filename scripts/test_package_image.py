import hashlib
import io
import json
import tarfile
import tempfile
import unittest
from pathlib import Path

from package_image import inspect_archive


class ImageExportTests(unittest.TestCase):
    def make_archive(self, path, *, corrupt_layer=False):
        sha = "a" * 40
        blobs = {}

        def blob(data, media_type):
            digest = "sha256:" + hashlib.sha256(data).hexdigest()
            blobs["blobs/sha256/" + digest[7:]] = data
            return {"digest": digest, "size": len(data), "mediaType": media_type}

        layer = blob(b"test filesystem", "application/vnd.oci.image.layer.v1.tar")
        config = {
            "os": "linux", "architecture": "amd64",
            "config": {"User": "1654", "Labels": {"org.opencontainers.image.revision": sha}},
            "rootfs": {"type": "layers", "diff_ids": [layer["digest"]]},
        }
        config_descriptor = blob(json.dumps(config).encode(), "application/vnd.oci.image.config.v1+json")
        manifest = blob(
            json.dumps({"schemaVersion": 2, "config": config_descriptor, "layers": [layer]}).encode(),
            "application/vnd.oci.image.manifest.v1+json",
        )
        blobs["index.json"] = json.dumps({"schemaVersion": 2, "manifests": [manifest]}).encode()
        blobs["oci-layout"] = b'{"imageLayoutVersion":"1.0.0"}'
        if corrupt_layer:
            blobs["blobs/sha256/" + layer["digest"][7:]] = b"bad filesystem!"
        with tarfile.open(path, "w") as archive:
            for name, data in blobs.items():
                member = tarfile.TarInfo(name)
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))
        return sha, {"Id": config_descriptor["digest"], "RootFS": {"Layers": [layer["digest"]]}}, manifest["digest"]

    def test_export_retains_built_identity_and_filesystem(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image.tar"
            sha, image, digest = self.make_archive(path)
            self.assertEqual(inspect_archive(path, image, sha), digest)
            with self.assertRaisesRegex(ValueError, "identity"):
                inspect_archive(path, {**image, "Id": "sha256:" + "f" * 64}, sha)
            with self.assertRaisesRegex(ValueError, "revision"):
                inspect_archive(path, image, "b" * 40)
            with self.assertRaisesRegex(ValueError, "filesystem"):
                inspect_archive(path, {**image, "RootFS": {"Layers": []}}, sha)

    def test_corrupt_layer_is_not_publishable(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image.tar"
            sha, image, _ = self.make_archive(path, corrupt_layer=True)
            with self.assertRaises(ValueError):
                inspect_archive(path, image, sha)


if __name__ == "__main__":
    unittest.main()
