#!/usr/bin/env python3
"""Check custom-port Caddy configuration without changing services or OS trust."""
import argparse
import http.client
import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--caddy", default=shutil.which("caddy"), help="Installed Caddy executable")
    args = parser.parse_args()
    if not args.caddy:
        parser.error("Install Caddy or supply --caddy /path/to/caddy")
    template = Path(__file__).with_name("Caddyfile.custom-port").read_text()
    ports = (1, 80, 443, 2019, 8443, 65535)
    with tempfile.TemporaryDirectory(prefix="protobot HTTPS config ") as directory:
        root = Path(directory)
        cert, key, config = root / "cert.pem", root / "key.pem", root / "Caddyfile"
        subprocess.run([
            "openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
            "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
            "-keyout", str(key), "-out", str(cert),
        ], check=True, capture_output=True)
        key.chmod(0o600)
        env = dict(os.environ, PROTOBOT_HOST="localhost", PROTOBOT_TLS_CERT=str(cert),
                   PROTOBOT_TLS_KEY=str(key), PROTOBOT_UPSTREAM="127.0.0.1:8787",
                   XDG_DATA_HOME=str(root / "data"), XDG_CONFIG_HOME=str(root / "config"))
        config.write_text(template)
        for port in ports:
            env["PROTOBOT_HTTPS_PORT"] = str(port)
            validated = subprocess.run([args.caddy, "validate", "--adapter", "caddyfile", "--config", str(config)], env=env, capture_output=True, text=True)
            if validated.returncode:
                raise RuntimeError(validated.stderr)
            adapted = subprocess.run([args.caddy, "adapt", "--adapter", "caddyfile", "--config", str(config)], env=env, capture_output=True, text=True, check=True)
            model = json.loads(adapted.stdout)
            assert model["admin"]["disabled"], "Unexpected admin listener"
            servers = list(model["apps"]["http"]["servers"].values())
            assert len(servers) == 1 and servers[0]["listen"] == [f":{port}"], "Unexpected public listener"
            assert servers[0]["tls_connection_policies"] and servers[0]["automatic_https"]["disable"], "TLS/automation configuration changed"

        class Backend(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b"protobot HTTPS proxy test")

            def log_message(self, *_):
                pass

        backend = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Backend)
        thread = threading.Thread(target=backend.serve_forever, daemon=True)
        thread.start()
        # The live probe uses only loopback and an available test port. The port
        # matrix above is validated without binding privileged/public listeners.
        with socket.socket() as available:
            available.bind(("127.0.0.1", 0))
            public_port = available.getsockname()[1]
        env["PROTOBOT_HTTPS_PORT"] = str(public_port)
        env["PROTOBOT_UPSTREAM"] = f"127.0.0.1:{backend.server_port}"
        config.write_text(template.replace("{\n", "{\n\tdefault_bind 127.0.0.1\n", 1))
        log = (root / "caddy.log").open("w")
        process = subprocess.Popen([args.caddy, "run", "--adapter", "caddyfile", "--config", str(config)], env=env, stdout=log, stderr=log)
        try:
            context = ssl.create_default_context(cafile=str(cert))
            deadline = time.monotonic() + 10
            while True:
                if process.poll() is not None:
                    raise RuntimeError("Temporary Caddy process exited")
                try:
                    connection = http.client.HTTPSConnection("localhost", public_port, context=context, timeout=1)
                    connection.request("GET", "/capabilities")
                    response = connection.getresponse()
                    assert response.status == 200 and response.read() == b"protobot HTTPS proxy test"
                    connection.close()
                    break
                except (ConnectionRefusedError, TimeoutError):
                    if time.monotonic() >= deadline:
                        raise
                    time.sleep(0.05)
        finally:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            log.close()
            backend.shutdown()
            backend.server_close()
            thread.join(timeout=2)
        print(json.dumps({"passed": True, "configured_tls_ports": ports, "verified_local_tls_proxy": True}))


if __name__ == "__main__":
    main()
