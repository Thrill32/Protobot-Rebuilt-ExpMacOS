#!/usr/bin/env python3
"""Read-only verification of retained collaboration packages; never repack/sign them."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import subprocess
import tarfile
import tempfile
import time
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def safe_name(name):
    path = PurePosixPath(name)
    require(not path.is_absolute() and ".." not in path.parts and "\\" not in name,
            "unsafe archive path: " + name)


def verify(artifacts, record, extracted):
    repo = Path(__file__).resolve().parents[2]
    catalog = (repo / "Assets/Resources/Collaboration/catalog.json").read_bytes()
    require(json.loads(catalog)["hash"] == record["catalog"], "source catalog differs from record")
    report = {"catalog": record["catalog"], "helpers": {}, "players": {}, "archives": {}}
    assemblies = ("protobot-collab.dll", "Server.dll", "Shared.dll")
    guides = ("INSTALLATION.md", "ROOM_MANAGEMENT.md", "ROOM_REPLACEMENT.md", "TLS.md", "NETWORK.md")
    for name, expected in record["archives"].items():
        archive = artifacts / name
        raw = archive.read_bytes()
        digest = sha(raw)
        require(digest == expected["sha256"] and len(raw) == expected["bytes"], "archive changed: " + name)
        sidecar = archive.with_name(name + ".sha256").read_text().split()
        require(sidecar == [digest, name], "checksum sidecar differs: " + name)
        report["archives"][name] = {"sha256": digest, "bytes": len(raw)}

    for rid, expected in record["helpers"].items():
        directory = artifacts / rid
        raw = (directory / "artifact.json").read_bytes()
        manifest = json.loads(raw)
        require(sha(raw) == expected["manifestSha256"], "manifest changed: " + rid)
        build = manifest["build"]
        require(manifest["schema"] == 1 and build["runtime"] == rid and
                build["catalog"] == record["catalog"] and build["build"] == expected["build"] and
                build["protocol"] == "0.9.0-alpha" and 3 in build["storage"] and
                "room-replacement-v1" in build["capabilities"], "build contract differs: " + rid)
        files = {item["path"]: item for item in manifest["files"]}
        require(len(files) == len(manifest["files"]), "duplicate manifest entries: " + rid)
        require(not any(p.is_symlink() for p in directory.rglob("*")), "linked helper entry: " + rid)
        actual = {p.relative_to(directory).as_posix() for p in directory.rglob("*") if p.is_file()}
        require(actual == set(files) | {"artifact.json"}, "helper inventory differs: " + rid)
        for name, item in files.items():
            safe_name(name)
            path = directory / name
            content = path.read_bytes()
            require(len(content) == item["length"] and sha(content) == item["sha256"], "helper integrity: " + str(path))
            if os.name != "nt" and item["executable"]:
                require(path.stat().st_mode & 0o100, "helper executable bit missing: " + str(path))
        require((directory / "catalog.json").read_bytes() == catalog, "helper catalog differs: " + rid)
        for guide in guides:
            require((directory / guide).read_bytes() == (repo / "Collaboration" / guide).read_bytes(), "helper guide differs: " + guide)
        for name in assemblies:
            require(sha((directory / name).read_bytes()) == expected["assemblies"][name], "assembly differs: " + rid + "/" + name)
        with tarfile.open(artifacts / ("protobot-collab-" + rid + ".tar.gz")) as packed:
            members = packed.getmembers()
            require(len({m.name for m in members}) == len(members), "duplicate tar entries: " + rid)
            for member in members:
                safe_name(member.name)
                require(member.isfile() or member.isdir(), "non-file tar entry: " + member.name)
            inventory = {m.name for m in members if m.isfile()}
            require(inventory == {rid + "/" + name for name in actual}, "tar inventory differs: " + rid)
            # Preserve member order: random seeks repeatedly decompress a gzip archive.
            for entry in members:
                if not entry.isfile():
                    continue
                name = entry.name[len(rid) + 1:]
                content = packed.extractfile(entry).read()
                require(content == (directory / name).read_bytes(), "tar payload differs: " + rid + "/" + name)
                if name in files and files[name]["executable"]:
                    require(entry.mode & 0o100, "tar executable bit missing: " + name)
            if rid == "osx-arm64" and extracted is not None:
                # All members were checked above. Publish only into a new test directory.
                extracted.mkdir(parents=True, exist_ok=False)
                packed.extractall(extracted, filter="data")
        report["helpers"][rid] = {"manifestSha256": sha(raw), "build": build["build"], "files": len(files)}

    for label, root, prefix, bundle, resource, rid in (
        ("Mac", artifacts / "players/Protobot.app", "Protobot.app/", "Contents/Helpers/Collaboration/",
         "Contents/Resources/Data/resources.assets", "osx-arm64"),
        ("Windows", artifacts / "players/Windows", "Windows/", "Collaboration/", "Protobot_Data/resources.assets", "win-x64"),
    ):
        require(not (root / bundle / "artifact.json").exists(), "runtime bundle claims complete installation manifest")
        with zipfile.ZipFile(artifacts / ("Protobot-" + label + "-collaboration.zip")) as packed:
            names = packed.namelist()
            require(len(set(names)) == len(names), "duplicate player archive entries: " + label)
            for name in names:
                safe_name(name)
            require(packed.testzip() is None, "player ZIP CRC failure: " + label)
            inventory = {name[len(prefix):] for name in names if name.startswith(prefix) and not name.endswith("/")}
            actual = {p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file() or p.is_symlink()}
            require(inventory == actual, "player archive inventory differs: " + label)
            for name in actual:
                path = root / name
                content = os.readlink(path).encode() if path.is_symlink() else path.read_bytes()
                require(packed.read(prefix + name) == content, "player archive payload differs: " + label + "/" + name)
            require(packed.read(prefix + bundle + "catalog.json") == catalog and catalog in packed.read(prefix + resource),
                    "player catalog differs: " + label)
            for name in assemblies:
                require(sha(packed.read(prefix + bundle + name)) == record["helpers"][rid]["assemblies"][name], "player helper assembly differs: " + label)
            for name in guides:
                require(packed.read(prefix + bundle + name) == (repo / "Collaboration" / name).read_bytes(), "player guide differs: " + label + "/" + name)
        report["players"][label] = {"files": len(actual), "archiveMatchesLooseFiles": True, "catalogAssembliesGuidesVerified": True}

    if platform.system() == "Darwin":
        targets = [artifacts / "osx-arm64/protobot-collab", artifacts / "players/Protobot.app"]
        if extracted is not None:
            targets.append(extracted / "osx-arm64/protobot-collab")
        for target in targets:
            subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(target)], check=True, capture_output=True)
        with tempfile.TemporaryDirectory(prefix="protobot-rc-signature-") as temporary:
            subprocess.run(["/usr/bin/ditto", "-x", "-k", str(artifacts / "Protobot-Mac-collaboration.zip"), temporary], check=True, capture_output=True)
            subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(Path(temporary) / "Protobot.app")], check=True, capture_output=True)
        report["macSignaturesIncludingExtractedArchives"] = True
        report["signatureScope"] = "Local code integrity only; notarization, Gatekeeper download/quarantine acceptance and publisher trust were not tested."
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, default=Path(__file__).resolve().parents[1] / "artifacts")
    parser.add_argument("--record", type=Path, required=True, help="Previously retained package checksum/build record")
    parser.add_argument("--output", type=Path, required=True, help="New evidence JSON; existing files are refused")
    parser.add_argument("--extract-native-to", type=Path, help="New test directory for the verified Mac helper")
    args = parser.parse_args()
    require(not args.output.exists(), "evidence destination already exists")
    require(args.extract_native_to is None or not args.extract_native_to.exists(), "extraction destination already exists")
    started = time.monotonic()
    report = verify(args.artifacts.resolve(), json.loads(args.record.read_text()), args.extract_native_to)
    report.update(passed=True, elapsedSeconds=round(time.monotonic() - started, 2), platform=platform.platform(),
                  packageRecordSha256=sha(args.record.read_bytes()), checkerSha256=sha(Path(__file__).read_bytes()),
                  sourceCommit=subprocess.check_output(
                      ["git", "-C", str(Path(__file__).resolve().parents[2]), "rev-parse", "HEAD"], text=True).strip())
    with args.output.open("x") as output:
        json.dump(report, output, indent=2)
        output.write("\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
