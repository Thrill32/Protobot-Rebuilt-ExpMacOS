#!/usr/bin/env python3
"""Two independent Mac players, real import UI and a substituted OS picker.

Use a new --output directory. All profiles, rooms, files and processes are disposable.
--baseline verifies the original false-success bug in an instrumented old player.
"""
import argparse
import json
import select
import struct
import subprocess
import time
import uuid
from pathlib import Path


def fixtures(root):
    root.mkdir()
    vertices = [(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]
    faces = [(0, 3, 2), (0, 2, 1), (4, 5, 6), (4, 6, 7), (0, 1, 5), (0, 5, 4),
             (3, 7, 6), (3, 6, 2), (0, 4, 7), (0, 7, 3), (1, 2, 6), (1, 6, 5)]
    (root / "cube.obj").write_text("".join("v %s %s %s\n" % v for v in vertices) +
                                   "".join("f %d %d %d\n" % tuple(i + 1 for i in f) for f in faces))
    records = [struct.pack("<12fH", 0, 0, 0, *vertices[f[0]], *vertices[f[1]], *vertices[f[2]], 0) for f in faces]
    for name, count in [("cube.stl", 12), ("large-claw-size.stl", 326000)]:
        with (root / name).open("wb") as stream:
            stream.write(b"Known-valid cube triangles".ljust(80, b" "))
            stream.write(struct.pack("<I", count))
            for i in range(count):
                stream.write(records[i % len(records)])
    assert (root / "large-claw-size.stl").stat().st_size == 16300084


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--cli", type=Path, help="Use a separate durable CLI server instead of in-app Host.")
    parser.add_argument("--catalog", type=Path, help="Explicit original catalog for an existing compatible room.")
    parser.add_argument("--baseline", action="store_true")
    parser.add_argument("--headless", action="store_true")
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    fixture_root = root / "fixtures"
    fixtures(fixture_root)
    app = args.player.resolve()
    executable = next(p for p in (app / "Contents/MacOS").iterdir() if not p.name.startswith("."))
    catalog = args.catalog.resolve() if args.catalog else app / "Contents/Helpers/Collaboration/catalog.json"
    processes, records = [], []

    def wait(condition, limit=150):
        deadline = time.monotonic() + limit
        while time.monotonic() < deadline:
            value = condition()
            if value:
                return value
            for process in processes:
                if process.poll() is not None:
                    raise RuntimeError("Test process exited: " + str(process.args))
            time.sleep(.05)
        raise TimeoutError("Custom-model player check timed out")

    def command(output, action, path=None):
        identifier = uuid.uuid4().hex
        temporary = Path(str(output) + ".command.tmp")
        temporary.write_text(json.dumps(dict(id=identifier, action=action, path=str(path) if path else None)))
        temporary.replace(str(output) + ".command")

        def completed():
            try:
                report = json.loads(output.read_text())
                return report if report.get("id") == identifier else None
            except (OSError, json.JSONDecodeError):
                return None

        report = wait(completed)
        records.append(dict(action=action, client=output.stem, report=report))
        (root / "steps.json").write_text(json.dumps(records, indent=2))
        if report.get("error"):
            raise RuntimeError(report["error"])
        if action != "inspect":
            print(output.stem, action, dict(revision=report["revision"], parts=len(report["bodies"]),
                  status=report["status"], notification=report["notification"]), flush=True)
        return report

    def launch(name, url=None):
        output = root / (name + ".json")
        options = [str(executable), "--collab-profile", str(root / (name + "-profile")),
                   "--collab-probe", "custom-model", "--collab-test-results", str(output),
                   "--collab-name", name, "-logFile", str(root / (name + ".log")),
                   "--collab-transport", "websocket" if name == "Host" else "long-polling",
                   "-screen-width", "1000", "-screen-height", "760", "-screen-fullscreen", "0"]
        if args.headless:
            options += ["-batchmode", "-nographics"]
        if args.catalog:
            options += ["--collab-catalog", str(catalog)]
        if url:
            options += ["--collab-server", url]
        with (root / (name + "-stdout.log")).open("w") as log:
            processes.append(subprocess.Popen(options, stdout=log, stderr=subprocess.STDOUT))
        wait(output.exists)
        assert not json.loads(output.read_text()).get("error")
        return output

    def converge(host, guest, count):
        def synchronized():
            h, g = command(host, "inspect"), command(guest, "inspect")
            return (h, g) if (h["hash"] == g["hash"] == h["clientHash"] == g["clientHash"] and
                              len(h["bodies"]) == len(g["bodies"]) == count) else None
        reports = wait(synchronized, 30)
        for report in reports:
            accepted = {p["id"]: p for p in report["accepted"]["parts"]}
            assert {b["id"] for b in report["bodies"]} == accepted.keys()
            for body in report["bodies"]:
                assert body["renderer"] and body["collider"], body
                if not args.baseline:
                    assert body["surfaceHit"], body
                if not args.headless and not args.baseline:
                    assert body["shaderSupported"], body
                assert all(abs(a - b) < .0001 for a, b in zip(body["position"], accepted[body["id"]]["position"]))
        return reports

    def start_server(listen):
        with (root / "server.log").open("a") as log:
            process = subprocess.Popen([str(args.cli.resolve()), "serve", "--data-dir", str(root / "server-room"),
                                        "--listen", listen], stdout=subprocess.PIPE, stderr=log, text=True)
        processes.append(process)
        wait(lambda: select.select([process.stdout], [], [], 0)[0])
        ready = json.loads(process.stdout.readline())
        (root / "server-ready.json").write_text(json.dumps(ready))
        return process, ready["url"]

    try:
        if args.cli:
            with (root / "server-init.log").open("w") as log:
                subprocess.run([str(args.cli.resolve()), "init", "--data-dir", str(root / "server-room"),
                                "--room", "team", "--catalog", str(catalog)], check=True, stdout=log, stderr=subprocess.STDOUT)
            server, url = start_server("http://127.0.0.1:0")
            host = launch("Host", url)
            assert command(host, "join")["connected"]
        else:
            host = launch("Host")
            assert command(host, "host")["connected"]
            url = Path(str(host) + ".endpoint").read_text()
        guest = launch("Editor", url)
        assert command(guest, "join")["connected"]
        count = 0
        for output in [host, guest]:
            for extension in ["obj", "stl"]:
                report = command(output, "import", fixture_root / ("cube." + extension))
                count += 1
                assert len(report["bodies"]) == count and any(b["selected"] for b in report["bodies"])
                if not args.baseline:
                    assert report["notification"].startswith("Imported cube into the shared room.")
                converge(host, guest, count)
        for output in [host, guest]:
            before = command(output, "inspect")
            report = command(output, "import", fixture_root / "large-claw-size.stl")
            assert report["hash"] == before["hash"] == report["clientHash"]
            assert len(report["bodies"]) == count and report["assets"] == before["assets"]
            if args.baseline:
                assert report["status"] == "invalid-server-response"
                assert report["notification"] == "Submitted large-claw-size to the shared room."
            else:
                assert "8 MiB encoded geometry limit" in report["status"] == report["notification"]
                if not args.headless:
                    command(output, "capture", root / (output.stem + "-limit.png"))
            converge(host, guest, count)
        if not args.baseline:
            # Every imported body can be selected and moved through real G confirmation.
            accepted = converge(host, guest, count)[0]
            for index, body in enumerate(accepted["bodies"]):
                command([host, guest][index % 2], "edit", body["id"])
                converge(host, guest, count)
            # Reconnect/reload must no longer depend on the import files.
            for source in fixture_root.iterdir():
                source.unlink()
            for output in [host, guest]:
                before = command(output, "inspect")
                command(output, "save", root / (output.stem + ".pbb"))
                after = command(output, "reconnect")
                assert after["participant"] == before["participant"] and after["hash"] == before["hash"]
                converge(host, guest, count)
                if not args.headless:
                    command(output, "capture", root / (output.stem + ".png"))
            saved_bodies = {b["id"]: b for b in converge(host, guest, count)[0]["bodies"]}
            for output in ([host, guest] if args.cli else [guest]):
                reloaded = command(output, "reload", root / (output.stem + ".pbb"))
                assert len(reloaded["bodies"]) == count and not reloaded["connected"]
                for body in reloaded["bodies"]:
                    expected = saved_bodies[body["id"]]
                    assert body["vertices"] == expected["vertices"] and body["triangles"] == expected["triangles"]
                    assert body["position"] == expected["position"] and body["surfaceHit"] and body["renderer"]
                assert command(output, "join")["connected"]
                converge(host, guest, count)
            before = converge(host, guest, count)
            if args.cli:
                processes.remove(server)
                server.terminate()
                server.wait(timeout=15)
                server, _ = start_server(url)
                for output, previous in zip([host, guest], before):
                    after = command(output, "reconnect")
                    assert after["participant"] == previous["participant"] and after["hash"] == previous["hash"]
                converge(host, guest, count)
        result = dict(passed=True, baseline=args.baseline, cli=bool(args.cli), rendered=not args.headless, parts=count)
        (root / "result.json").write_text(json.dumps(result, indent=2))
        print("PASS", root, flush=True)
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


if __name__ == "__main__":
    main()
