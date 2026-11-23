#!/usr/bin/env python3
"""Drive real pointer Host locally / direct-ID viewer joining in two isolated players."""
import argparse
import json
import subprocess
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    app = args.player.resolve()
    executable = next(p for p in (app / "Contents/MacOS").iterdir() if not p.name.startswith(".")) if app.suffix == ".app" else app
    output = root / "navigation.json"
    command = [str(executable), "--collab-profile", str(root / "owner-profile"),
               "--collab-probe", "ui", "--collab-test-results", str(output), "-logFile", str(root / "owner.log"),
               "-screen-width", "1280", "-screen-height", "720", "-screen-fullscreen", "0"]
    process = subprocess.Popen(command)
    try:
        deadline = time.monotonic() + 150
        while not output.exists():
            if process.poll() is not None:
                raise RuntimeError("Navigation player exited; inspect owner.log")
            if time.monotonic() > deadline:
                raise TimeoutError("Navigation players timed out; inspect isolated logs")
            time.sleep(.1)
        owner = json.loads(output.read_text())
        if not owner.get("passed"):
            raise RuntimeError(owner.get("error", "Navigation owner probe failed"))
        child = json.loads(Path(str(output) + ".child.json").read_text())
        assert owner["passed"] and owner["role"] == "owner" and owner["participants"] == 2, owner
        assert child["passed"] and child["role"] == "viewer", child
        assert owner["transport"] == child["transport"] == "websocket", (owner, child)
        assert len(list(root.glob("*.png"))) == 4, "Missing rendered navigation evidence"
        (root / "summary.json").write_text(json.dumps(dict(passed=True, owner=owner, viewer=child, localOnly=True), indent=2) + "\n")
        print("Navigation players passed: pointer Host locally / direct room ID, owner + viewer, four renders; " + str(root))
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=20)


if __name__ == "__main__":
    main()
