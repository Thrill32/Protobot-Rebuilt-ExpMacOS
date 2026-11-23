#!/usr/bin/env python3
"""Two independent Mac players, real import UI and a substituted OS picker.

Use a new --output directory. All profiles, rooms, files and processes are disposable.
--baseline verifies the original false-success bug in an instrumented old player.
"""
import argparse
import json
import select
import shutil
import struct
import subprocess
import time
import uuid
import zlib
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
                if count == 12:
                    stream.write(records[i % len(records)])
                else:
                    # Binary-exact fractional coordinates expand beyond 32 MiB in JSON.
                    shift = (i % 997) / 1024
                    points = [value + shift for index in faces[i % len(faces)] for value in vertices[index]]
                    stream.write(struct.pack("<12fH", 0, 0, 0, *points, 0))
    assert (root / "large-claw-size.stl").stat().st_size == 16300084
    with (root / "over-limit.stl").open("wb") as stream:
        stream.write(b"Valid source; encoded geometry exceeds 64 MiB".ljust(80, b" "))
        stream.write(struct.pack("<I", 500000))
        for i in range(500000):
            shift = (i % 997) * .0001234567
            points = [value + shift for index in faces[i % len(faces)] for value in vertices[index]]
            stream.write(struct.pack("<12fH", 0, 0, 0, *points, 0))


def appearance_fixtures(root):
    root.mkdir()
    assets = root / "finish"
    assets.mkdir()
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    size = 64
    pixels = b"".join(b"\0" + b"".join(bytes((250, 225, 42) if (x // 16 + y // 16) % 2 else (0, 125, 240))
                         for x in range(size)) for y in range(size))
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b"")
    (assets / "checker.png").write_bytes(png)
    subprocess.run(["/usr/bin/sips", "-s", "format", "jpeg", str(assets / "checker.png"),
                    "--out", str(assets / "checker.jpg")], check=True, stdout=subprocess.DEVNULL)
    details = ("v -2 -1 0\nv 0 -1 0\nv 0 1 0\nv -2 1 0\nv 2 -1 0\nv 2 1 0\n"
               "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n"
               "usemtl red\nf 1 2 3 4\nusemtl checker\nf 2/1 5/2 6/3 3/4\n")
    for name, ext in (("owner-png", "png"), ("editor-jpeg", "jpg"), ("missing-image", "missing"), ("oversize-image", "huge")):
        filename = f"checker.{ext}" if ext in ("png", "jpg") else ext + ".png"
        (assets / f"{name}.mtl").write_text(f"newmtl red\nKd .85 .12 .18\nnewmtl checker\nKd 1 1 1\nmap_Kd {filename}\n")
        (root / f"{name}.obj").write_text(f"mtllib finish/{name}.mtl\n" + details)
    oversized = chunk(b"IHDR", struct.pack(">IIBBBBB", 2049, size, 8, 2, 0, 0, 0))
    (assets / "huge.png").write_bytes(png[:8] + oversized + png[33:])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--cli", type=Path, help="Use a separate durable CLI server instead of in-app Host.")
    parser.add_argument("--catalog", type=Path, help="Explicit original catalog for an existing compatible room.")
    parser.add_argument("--baseline", action="store_true")
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--appearance", action="store_true", help="Exercise preserved appearance with an owned helper and late-joining editor.")
    args = parser.parse_args()
    if args.appearance and (args.cli or args.baseline):
        parser.error("--appearance uses a current player and its owned helper")
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    fixture_root = root / "fixtures"
    (appearance_fixtures if args.appearance else fixtures)(fixture_root)
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

    def appearance():
        host = launch("Host")
        offline = command(host, "import", fixture_root / "owner-png.obj")
        assert not offline["shared"] and offline["notification"].startswith("Imported owner-png.")
        assert offline["bodies"][0]["textureSizes"] == ["", "64x64"]
        undone = command(host, "undo")
        assert not undone["bodies"] and undone["deletedAppearanceBytes"] > 0
        assert command(host, "redo")["bodies"][0]["textureSizes"] == ["", "64x64"]
        assert command(host, "undo")["deletedAppearanceBytes"] > 0
        assert command(host, "import", fixture_root / "owner-png.obj")["deletedAppearanceBytes"] == 0
        command(host, "save", root / "Offline.pbb")
        assert not command(host, "new")["bodies"]
        assert command(host, "host-editors")["role"] == "owner"
        first = command(host, "import", fixture_root / "owner-png.obj")
        assert first["notification"].startswith("Imported owner-png into the shared room.")
        assert len(first["bodies"]) == 1 and first["bodies"][0]["selected"]
        url = Path(str(host) + ".endpoint").read_text()
        guest = launch("Editor", url)
        assert command(guest, "join")["role"] == "editor"

        def visual(body, painted=False):
            assert body["submeshes"] == 2 and body["triangles"] == 4 and body["uv"] == body["vertices"] > 0, body
            assert body["painted"] is painted, body
            if painted:
                assert body["textureSizes"] == ["", ""] and body["colors"][0] == body["colors"][1], body
            else:
                assert body["textureSizes"] == ["", "64x64"], body
                assert abs(body["colors"][0][0] - .85) < .00001 and abs(body["colors"][0][1] - .12) < .00001, body

        for report in converge(host, guest, 1):
            visual(report["bodies"][0])
        second = command(guest, "import", fixture_root / "editor-jpeg.obj")
        assert second["notification"].startswith("Imported editor-jpeg into the shared room.")
        owner_id = first["bodies"][0]["id"]
        editor_id = next(b["id"] for b in second["bodies"] if b["selected"])
        command(guest, "edit", editor_id)
        for report in converge(host, guest, 2):
            for body in report["bodies"]:
                visual(body)
        for output, name in ((host, "missing-image"), (guest, "oversize-image")):
            before = command(output, "inspect")
            failed = command(output, "import", fixture_root / f"{name}.obj")
            assert failed["hash"] == before["hash"] == failed["clientHash"] and failed["assets"] == before["assets"], failed
            assert "Could not import model" in failed["notification"] or "2048 pixels" in failed["notification"], failed

        command(host, "copy", owner_id)
        assert converge(host, guest, 3)[0]["assets"] == 2
        command(guest, "mount", editor_id)
        assert next(b for b in converge(host, guest, 3)[0]["bodies"] if b["id"] == editor_id)["mounts"] == 1
        for action, painted in (("paint", True), ("undo", False), ("redo", True), ("default", False)):
            command(host, action, owner_id)
            for report in converge(host, guest, 3):
                for body in report["bodies"]:
                    visual(body, painted and body["id"] == owner_id)

        # Move and save the models while the room is live, then remove all source files.
        for output in (host, guest):
            command(output, "clipboard")
            command(output, "save", root / (output.stem + ".pbb"))
        shutil.rmtree(fixture_root)
        before = converge(host, guest, 3)
        restarted = command(host, "restart-owned")
        assert restarted["connected"] and restarted["participant"] == before[0]["participant"] and restarted["hash"] == before[0]["hash"]
        resumed = command(guest, "reconnect")
        assert resumed["connected"] and resumed["participant"] == before[1]["participant"] and resumed["hash"] == before[1]["hash"]
        for output, report in zip((host, guest), converge(host, guest, 3)):
            for body in report["bodies"]:
                visual(body)
            if not args.headless:
                command(output, "capture", root / (output.stem + ".png"))
        reloaded = command(guest, "reload", root / "Editor.pbb")
        assert not reloaded["shared"] and len(reloaded["bodies"]) == 3
        for body in reloaded["bodies"]:
            visual(body)
        assert command(guest, "replacement-inspect")["replacementConfirmation"]
        replaced = command(guest, "replacement-confirm")
        assert "Replaced room" in replaced["status"] and not replaced["shared"], replaced
        wait(lambda: command(host, "inspect")["requiresResynchronization"])
        owner = command(host, "resynchronize")
        assert owner["connected"] and owner["participant"] == before[0]["participant"] and owner["assets"] == 2
        assert owner["hash"] != before[0]["hash"] and len(owner["bodies"]) == 3
        for body in owner["bodies"]:
            visual(body)
        return dict(passed=True, rendered=not args.headless, parts=3, assets=2,
                    roles=["owner", "editor"], replacement=True, offline=True)

    try:
        if args.appearance:
            result = appearance()
            (root / "result.json").write_text(json.dumps(result, indent=2))
            print("PASS", root, flush=True)
            return
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
            maximum = before.get("geometryAssetBytes", 8 * 1024 * 1024)
            if maximum > 8 * 1024 * 1024 and not args.baseline:
                count += 1
                assert len(report["bodies"]) == count and report["notification"].startswith("Imported large-claw-size into the shared room.")
                assert any(b["vertices"] == 978000 and b["triangles"] == 326000 and b["selected"] for b in report["bodies"])
            elif args.baseline:
                assert report["hash"] == before["hash"] == report["clientHash"] and len(report["bodies"]) == count
                assert report["status"] == "invalid-server-response"
                assert report["notification"] == "Submitted large-claw-size to the shared room."
            else:
                assert report["hash"] == before["hash"] == report["clientHash"] and report["assets"] == before["assets"]
                assert "8 MiB encoded geometry limit" in report["status"] == report["notification"]
                assert "Update the collaboration server" in report["notification"]
            if not args.baseline:
                before = command(output, "inspect")
                report = command(output, "import", fixture_root / "over-limit.stl")
                assert report["hash"] == before["hash"] == report["clientHash"] and report["assets"] == before["assets"]
                assert f"{maximum // (1024 * 1024)} MiB encoded geometry limit" in report["status"] == report["notification"]
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
