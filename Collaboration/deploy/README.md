# Dedicated Debian room

This is the single-room deployment path for connecting from another network. The CLI runs independently; remote players connect to its public HTTPS hostname. No Unity runtime runs on Debian. Bounded custom geometry, confirmed live mounting layouts and sprocket chains are accepted room content. A separate large-asset service and multi-room administration remain unfinished. Offline single-room backup/restore is supported.

Choose the artifact matching `uname -m`: use `linux-x64` for `x86_64`, or `linux-arm64` for `aarch64`/`arm64` systems such as a 64-bit Raspberry Pi 4. These self-contained .NET 10 publishes include the managed runtime, but still require the distribution's native runtime dependencies. Use Microsoft's [Debian/runtime prerequisites](https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian) if a native dependency is missing. A 32-bit `armv7l` operating system is not supported by the supplied artifacts.

## Prepare the matching release

On the development computer, publish with the pinned SDK:

```sh
cd Collaboration
bash publish.sh linux-x64   # x86_64
bash publish.sh linux-arm64 # aarch64 / arm64
```

Copy the **entire** matching `artifacts/linux-<architecture>` directory to the Debian computer. Copy `catalog.json` from the exact player release that everyone will use (on macOS: `Protobot.app/Contents/Helpers/Collaboration/catalog.json`). To seed an existing catalog build, use **Collaboration → Export current build for CLI...** and copy that JSON too. It contains accepted parts/chains and the matching catalog, without passwords, local file paths or history. For an empty room, the catalog alone is enough. Never send a legacy `.pbb` file to the CLI.

On Debian, from the directory containing the copied artifact directory and catalog:

```sh
sudo useradd --system --user-group --home-dir /var/lib/protobot-collab --shell /usr/sbin/nologin protobot
sudo install -d -m 0755 /opt/protobot-collab
sudo cp -a linux-arm64/. /opt/protobot-collab/ # use linux-x64 on x86_64
sudo chmod 0755 /opt/protobot-collab/protobot-collab
sudo install -m 0644 catalog.json /opt/protobot-collab/catalog.json
sudo install -d -m 0700 -o protobot -g protobot /var/lib/protobot-collab
```

Create a new room (the `team` directory must not exist yet):

```sh
sudo -u protobot /opt/protobot-collab/protobot-collab init \
  --data-dir /var/lib/protobot-collab/team --room team \
  --catalog /opt/protobot-collab/catalog.json --password-prompt
```

For an existing build, replace `--catalog ...` with `--snapshot /path/readable/by/protobot/room-export.json`. `init` starts a fresh document generation and personal history from the exported accepted parts and chains. This is content transfer, not a recovery backup. It writes the password verifier and a private `owner.json` capability inside the room directory, and never prints their values. Keep the owner file private; it is separate from the join password. Blank password permits joining without a password. For noninteractive initialization, use `--password-file /protected/path` instead; do not put a password in command arguments.

## Run the service

Copy this repository's `protobot-collab.service` to Debian, then:

```sh
sudo install -m 0644 protobot-collab.service /etc/systemd/system/protobot-collab.service
sudo systemctl daemon-reload
sudo systemctl enable --now protobot-collab
sudo systemctl status protobot-collab
sudo journalctl -u protobot-collab -n 30 --no-pager
```

The unit runs an unprivileged process, restarts failures and uses the same SQLite room on restart. The readiness JSON's `url` is its **loopback listener**, not an internet invite. Standalone `serve` also accepts `--public-url https://cad.example.org` to label an advertised origin in readiness; this does not test reachability. The service lifecycle follows the normal [ASP.NET Core systemd hosting model](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0).

## Choose the listening ports

The public HTTPS port and private backend port are independent. The CLI `--listen` option accepts any valid port, including `0` to allocate a free backend port; in-app Host has the same numeric choice. Ports below 1024 require suitable OS permissions on Linux. A port already owned by another process cannot be reused by a second listener on the same address.

For example, keep the application backend private on 8787 and expose only public HTTPS on 80, 443, 8443 or another available port through Caddy. If you change the backend, edit the systemd unit's `--listen http://127.0.0.1:<port>` and use the same upstream in Caddy. Share `https://cad.example.org:<public-port>` with participants; an omitted HTTPS port means 443. `doctor`, Join, clipboard invites and the WebSocket/request transports retain explicit ports.

Direct Kestrel TLS also accepts `--listen https://0.0.0.0:<port>` when its certificate is supplied through the normal protected Kestrel configuration. Port selection alone does not create a certificate.

## Expose HTTPS with automatic certificates

Use a public hostname whose DNS A/AAAA records point to the server's reachable public addresses. For the default `Caddyfile`, forward inbound TCP 80 and 443 to the Debian computer (or permit inbound IPv6 traffic). Keep the backend private. A public IPv4 behind carrier-grade NAT cannot be reached by ordinary router port forwarding; resolve that with the ISP or use an existing reachable server. Do not publish an unusable AAAA record. Test from a different network, since a router's loopback behavior can differ.

Install Caddy using its [official Debian instructions](https://caddyserver.com/docs/install#debian-ubuntu-raspbian). Put the supplied Caddyfile block in `/etc/caddy/Caddyfile`, replacing `cad.example.org` with your real hostname. If Caddy already serves other sites, add this site block to that configuration.

```sh
sudo caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy
sudo journalctl -u caddy -n 30 --no-pager
```

Caddy obtains/renews a public certificate when the hostname and inbound reachability are configured. Its default [automatic HTTPS requirements](https://caddyserver.com/docs/automatic-https) include DNS and access to ports 80/443. The [reverse proxy supports WebSocket upgrades](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy); ordinary held HTTP requests use the same origin. Use the **HTTPS** hostname in clients, not the backend address or an HTTP redirect. No certificate-validation bypass or general tunnel is needed.

## HTTPS on 80, 443 or a custom public port

Use `Caddyfile.custom-port` when an existing service occupies 443 or when you want a specific HTTPS port. It loads PEM certificate/key files, disables automatic certificate management/redirects, and exposes the selected HTTPS port without claiming 443 or opening a plaintext HTTP listener. The Caddy admin listener is disabled too, so it cannot collide with a selected port such as 2019. This is a complete alternative configuration; do not merge its global settings into a Caddy instance that already manages other sites.

Obtain a certificate valid for your hostname, either from your existing certificate manager or via [DNS validation](https://caddyserver.com/docs/automatic-https#dns-challenge). DNS validation does not require an inbound validation listener. Certificates identify hostnames rather than port numbers, so the same valid hostname certificate can serve port 80, 443 or another port. Keep the key readable only by the certificate manager and Caddy. This template uses Caddy's [PEM certificate loading](https://caddyserver.com/docs/caddyfile/directives/tls); renewal stays with your certificate manager.

For example, create `/etc/caddy/protobot.env` with non-secret paths/settings:

```ini
PROTOBOT_HOST=cad.example.org
PROTOBOT_HTTPS_PORT=80
PROTOBOT_TLS_CERT=/etc/protobot-collab/tls/fullchain.pem
PROTOBOT_TLS_KEY=/etc/protobot-collab/tls/privkey.pem
PROTOBOT_UPSTREAM=127.0.0.1:8787
```

Set `PROTOBOT_HTTPS_PORT` to any available port from 1 through 65535. In `sudo systemctl edit caddy`, add:

```ini
[Service]
EnvironmentFile=/etc/caddy/protobot.env
```

Copy `Caddyfile.custom-port` to `/etc/caddy/Caddyfile`, then validate it with the same service environment and restart:

```sh
sudo sh -c 'set -a; . /etc/caddy/protobot.env; exec caddy validate --config /etc/caddy/Caddyfile'
sudo systemctl daemon-reload
sudo systemctl restart caddy
```

Use a restart rather than `caddy reload` in this mode because the admin listener is off. Restart Caddy after your certificate manager renews/replaces the PEM files. A DNS-based renewal job avoids competing for ports 80/443. Plain HTTP certificate validation on 80 cannot share that listener while it is serving TLS. If an existing certificate service is using 443, leave it running and configure the new Caddy instance only for the chosen free port.

Forward/allow the chosen public TCP port and connect with the explicit scheme and port:

```sh
protobot-collab doctor --url https://cad.example.org:80 \
  --catalog catalog.json --room team --password-prompt --transport all
```

Use the same `https://cad.example.org:80` in the player's Join page, with LAN HTTP off. Use `:8443` or another selected value in the same way. A network may restrict traffic by protocol as well as port; choosing 80 does not guarantee that a network accepts TLS on 80. Validate from the actual client network.

The CLI, WebSocket upgrades, commits and held requests all use that single public HTTPS origin. Only Caddy talks to the private application backend. If Caddy already manages the service on 443, use an additional site on its existing instance where appropriate, rather than launching competing listeners or stopping the existing service.

With Caddy, Python 3 and OpenSSL installed, validate this template locally without changing OS trust or services:

```sh
python3 validate-https-config.py --caddy /path/to/caddy
```

The check provisions the exact template for ports 1, 80, 443, 2019, 8443 and 65535 without binding those ports, then verifies HTTPS proxy traffic using a temporary loopback listener and certificate trusted only inside the test.

## Check from a client network, then join

First check the backend on Debian:

```sh
/opt/protobot-collab/protobot-collab doctor --url http://127.0.0.1:8787
```

From the network where players will connect, use the matching CLI and catalog:

```sh
protobot-collab doctor --url https://cad.example.org \
  --catalog catalog.json --room team --password-prompt \
  --transport all --output connection-report.json
```

Without `--catalog`, doctor checks capabilities/health and explicitly skips authenticated transports. With a catalog, it joins a read-only test member, checks the real shared client's initial and sustained event delivery, then leaves. Default `all` tests WebSocket and requests separately, so it can report WebSocket failure alongside working requests. Each sustained test takes at least 21 seconds; `--quick` skips that check and labels it skipped. Each transport probe consumes one retained membership record (the alpha room limit is 1,000 total); it never creates edits or personal undo history. Reports contain stable diagnostic codes, version/catalog/room and timing, without passwords/tokens. Exit codes: 0 selected checks passed, 2 a check failed, 1 invalid configuration. Skipped checks do not establish transport acceptance.

If WebSocket fails but requests pass, run `--transport requests` to verify that mode alone, then select **HTTPS / HTTP requests only** in the player's Join page. **Test server connection** performs the corresponding read-only test inside the actual Unity runtime before joining. Leave LAN HTTP off for this public HTTPS connection. Share only the HTTPS origin and room ID; enter the password separately. Match builds/catalogs across all participants.

| Result | Next check |
| --- | --- |
| `dns-failed` | Hostname and A/AAAA records |
| `connection-refused`, `network-unreachable`, `request-timeout` | Service, router forwarding, firewall, address family and reachability from another network |
| `tls-trust-or-name-failed` | Public certificate hostname/chain and the computer's normal trust configuration |
| `http-407` | The network's required proxy authentication/configuration |
| `http-301` / `http-302` / `http-307` / `http-308` | Enter the final HTTPS origin directly; doctor does not follow redirects |
| `server-version-mismatch`, `catalog-or-room-mismatch` | Same helper/player release, exported catalog and room ID |
| `wrong-password` | Room password entered separately |
| WebSocket failure with requests passing | Use requests mode; verify sustained delivery on that actual network |
| `event-stream-timeout` / `event-stream-disconnected` | Proxy buffering, held-request/upgrade restrictions, idle timeouts, or a stopped server |

Local automated checks establish CLI/protocol behavior, not approval or compatibility on a particular deployment network. Record that actual result after testing. The CLI runs on .NET and the players on Unity's runtime; a CLI success complements the player connection check.

## Stop and preserve the room

```sh
sudo systemctl stop protobot-collab
```

SQLite recovery is independent of every participant's local `.pbb` saves. Use offline backup as the service account while systemd is stopped. Create a private backup parent once, then choose a new archive and destination each time:

```sh
sudo install -d -o protobot -g protobot -m 0700 /var/lib/protobot-collab/backups
sudo -u protobot /opt/protobot-collab/protobot-collab backup \
  --data-dir /var/lib/protobot-collab/team \
  --output /var/lib/protobot-collab/backups/team-backup.zip
sudo -u protobot /opt/protobot-collab/protobot-collab restore \
  --input /var/lib/protobot-collab/backups/team-backup.zip \
  --data-dir /var/lib/protobot-collab/restored-team
```

Adjust paths to the installed helper and room. Backup refuses a running/locked room and never overwrites an archive. Restore refuses every existing destination, including an empty directory, validates in private staging, then publishes atomically. Do not copy individual SQLite/WAL files or remove `room.lock` to bypass ownership. Archive v1 contains a consistent standalone SQLite snapshot, versioned integrity manifest and matching `owner.json`; it preserves room identity, password verification, memberships/revocations, receipts, assets, mounts, explicit chains and undo/redo history. Keep the matching helper/catalog release with the archive. Archives contain credentials and remain unencrypted, with 0600 files and 0700 restored directories on Unix. Back up service configuration and TLS material separately.

To use the restore, update the systemd unit's `--data-dir` to `/var/lib/protobot-collab/restored-team`, run `sudo systemctl daemon-reload`, then `sudo systemctl start protobot-collab`. Keep the original stopped; never serve both copies. Verify authenticated sustained `doctor` checks and both participants' identity/history recovery through the original endpoint. Do not run `init` over recovered data. See [format, bounds and compatibility](../README.md#offline-backup-and-restore); online backup, storage/catalog migration and multi-room administration remain future work.
