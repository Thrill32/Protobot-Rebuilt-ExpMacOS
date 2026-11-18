#!/usr/bin/env python3
"""Two isolated development players: local editor replacement and owner resync.

Uses the existing development file-command probe and substitutes only native file
pickers. Requires a new output directory. Keep it for reports, saves and screenshots.
"""
import argparse
import hashlib
import json
import subprocess
import time
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--headless", action="store_true")
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    app = args.player.resolve()
    executable = next(p for p in (app / "Contents/MacOS").iterdir() if not p.name.startswith(".")) if app.suffix == ".app" else app
    records, processes = [], []

    def wait(condition, seconds=120):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            result = condition()
            if result:
                return result
            if any(p.poll() is not None for p in processes):
                raise RuntimeError("A test player exited; inspect its isolated log")
            time.sleep(.05)
        raise TimeoutError("Two-player replacement check timed out")

    def command(output, action, path=None):
        identifier = uuid.uuid4().hex
        temporary = Path(str(output) + ".command.tmp")
        temporary.write_text(json.dumps(dict(id=identifier, action=action, path=str(path) if path else None)))
        temporary.replace(str(output) + ".command")

        def received():
            try:
                report = json.loads(output.read_text())
                return report if report["id"] == identifier else None
            except (OSError, json.JSONDecodeError):
                return None

        result = wait(received)
        records.append(dict(client=output.stem, action=action, report=result))
        (root / "steps.json").write_text(json.dumps(records, indent=2))
        assert not result.get("error"), result.get("error")
        return result

    def launch(directory, name, origin=None):
        output = directory / (name + ".json")
        options = [str(executable), "--collaboration", "--collab-profile", str(directory / (name + "-profile")),
                   "--collab-probe", "editor-replacement", "--collab-test-results", str(output), "--collab-name", name,
                   "--collab-transport", "websocket" if name == "Owner" else "long-polling",
                   "-logFile", str(directory / (name + ".log")), "-screen-width", "1100", "-screen-height", "820", "-screen-fullscreen", "0"]
        if origin:
            options += ["--collab-server", origin]
        if args.headless:
            options += ["-batchmode", "-nographics"]
        with (directory / (name + "-stdout.log")).open("w") as log:
            processes.append(subprocess.Popen(options, stdout=log, stderr=subprocess.STDOUT))
        wait(output.exists)
        return output

    def digest(file):
        return hashlib.sha256(file.read_bytes()).hexdigest()

    try:
        for allowed in [False, True]:
            directory = root / ("yes" if allowed else "no")
            directory.mkdir()
            sources = []
            for name, scale in [("old", 1), ("local", 3)]:
                source = directory / (name + ".obj")
                source.write_text(f"v 0 0 0\nv {scale} 0 0\nv 0 {scale} 0\nv 0 0 {scale}\nf 1 3 2\nf 1 2 4\nf 1 4 3\nf 2 3 4\n")
                sources.append(source)
            owner = launch(directory, "Owner")
            command(owner, "import", sources[0])
            host = command(owner, "host-editors" if allowed else "host")
            assert host["connected"] and host["role"] == "owner"
            old = command(owner, "save", directory / "owner.pbb")
            old_bytes, owner_id = digest(directory / "owner.pbb"), old["participant"]
            editor = launch(directory, "Editor", Path(str(owner) + ".endpoint").read_text())
            command(editor, "import", sources[1])
            local = command(editor, "save", directory / "editor-local.pbb")
            local_bytes = digest(directory / "editor-local.pbb")

            def unchanged(result):
                assert not result["shared"] and result["localHash"] == local["localHash"]
                assert result["localSaved"] and result["localFile"] == str(directory / "editor-local.pbb")
                assert digest(directory / "editor-local.pbb") == local_bytes

            inspected = command(editor, "replacement-inspect")
            unchanged(inspected)
            if not allowed:
                assert not inspected["replacementConfirmation"] and "requires a room owner" in inspected["status"]
                assert command(owner, "inspect")["hash"] == old["hash"]
            else:
                assert inspected["replacementConfirmation"]
                if not args.headless:
                    command(editor, "capture", directory / "editor-confirmation.png")
                unchanged(command(editor, "replacement-cancel"))
                command(editor, "replacement-inspect")
                result = command(editor, "replacement-confirm")
                unchanged(result)
                assert "Replaced room" in result["status"]
                paused = wait(lambda: (r if (r := command(owner, "inspect"))["requiresResynchronization"] else None))
                assert not paused["connected"] and paused["hash"] == old["hash"] and paused["localHash"] == old["localHash"]
                assert digest(directory / "owner.pbb") == old_bytes
                resumed = command(owner, "resynchronize")
                assert resumed["connected"] and resumed["participant"] == owner_id and resumed["localHash"] == local["localHash"]
                assert resumed["hash"] == resumed["clientHash"] and not resumed["localSaved"]
                assert digest(directory / "owner.pbb") == old_bytes
                assert all(b["renderer"] and b["collider"] for b in resumed["bodies"])
                if not args.headless:
                    assert all(b["shaderSupported"] for b in resumed["bodies"])
                    command(owner, "capture", directory / "owner-resynchronized.png")
                attempts = list((directory / "Editor-profile/Collaboration/room-replacements").glob("*.json"))
                assert len(attempts) == 1
                unchanged(command(editor, "replacement-check", attempts[0]))
            for process in processes:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait()
            processes.clear()
        (root / "result.json").write_text(json.dumps(dict(passed=True, steps=len(records), policies=["no", "yes"], realPlayers=2), indent=2))
        print("Two-player editor replacement/resynchronization passed; evidence:", root)
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill(); process.wait()


if __name__ == "__main__":
    main()
