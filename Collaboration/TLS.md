# Direct HTTPS hosting with an existing certificate

The shared-server Create Room, owned-room Check/Join/replacement/outcome checks and server-admin views use this same HTTPS listener and ordinary platform certificate/name validation. No special creation trust mechanism is added. See [room-management policies and private ownership](ROOM_MANAGEMENT.md).

Standalone `serve` accepts `--tls-file /private/local/tls.json`. This is an alternative to the existing [Caddy deployment](deploy/README.md), for operators who already have a certificate and private key in a **PKCS#12 / PFX** file. Certificate issuance, renewal, ACME and trust-store management remain the operator's responsibility. Normal in-app Host/Resume keeps its private Direct loopback workflow; participants enter only the HTTPS origin, room and room password.

## Private configuration

Create a private directory and edit the following file there. Replace the password placeholder **inside the file**, never in a shell command, argument, environment variable or service log:

```json
{
  "format": "protobot-tls",
  "schema": 1,
  "certificate": "server.pfx",
  "password": "ENTER PRIVATELY"
}
```

All four fields are required; `password` may be an empty string for an unencrypted PFX. Relative certificate paths resolve beside the configuration file. Absolute local paths are also supported. Unknown/duplicate fields, unsupported versions, malformed JSON and null values are refused. The configuration is at most **4 KiB**, certificate path at most **2,048 characters**, password at most **1,024 characters**, and PFX at most **1 MiB**. PFX loading retains .NET's KDF work limits and permits at most **eight certificates and one key**. Supply the server leaf/private key and any required intermediate certificates in that PFX. The leaf must be currently valid and permit server authentication when an enhanced-key-usage extension is present.

Both files must be regular private files, with no symlink at the file itself. On Unix use a private directory (0700) and files with no group/other access (normally 0600), readable by the service account. On Windows the files must be owned by the current service/operator account and allow access only to that account. Broad permissions are refused, never silently repaired. This configuration is separate from room bootstrap, server registry, owner/admin files and outbound proxy settings. No certificate bytes/password/path enter readiness, invitations, diagnostic exports, room databases, backups or participant recovery. Managed password strings have their normal runtime lifetime; no secret environment/argument or preference storage is added.

## Start and inspect

Initialize a room or registry using the existing commands, then choose a literal IPv4/IPv6 binding address and HTTPS port. Port zero allocates a local test port; public service deployments should choose a stable port. For example:

```sh
protobot-collab serve --data-dir ./team \
  --listen https://0.0.0.0:8443 --tls-file ./private/tls.json \
  --public-url https://cad.example.org:8443

protobot-collab serve --multi-room --config ./server/server.json \
  --listen 'https://[::]:8443' --tls-file ./private/tls.json \
  --public-url https://cad.example.org:8443

protobot-collab doctor --url https://cad.example.org:8443 \
  --catalog ./catalog.json --room team --password-prompt --transport all
```

The explicit TLS file requires `https://<literal-IP>:<port>`; DNS belongs in the advertised/client origin and certificate. IPv4 and IPv6 wildcard binding/reachability follow the platform's normal behavior. This option creates **one HTTPS listener** and replaces ambient Kestrel endpoint/default-certificate configuration, including ambient HTTP endpoints. An unreadable/invalid certificate, wrong password or bind failure stops startup; no plaintext listener or alternate certificate is substituted. Omitting `--tls-file` preserves existing HTTP/Caddy and ambient Kestrel HTTPS behavior. `--bootstrap-stdin` refuses the explicit TLS option: extending owned-host TLS/control is separate work.

Readiness `url` describes the actual bound listener (a wildcard address is not an invite). Optional `publicUrl` is the operator's advertised HTTPS origin. Neither proves routing, firewall access, hostname validity or client trust. Direct HTTPS has no additional plaintext administration listener: use the trusted hostname and the same HTTPS port for owner/admin CLI operations. The server never changes OS trust, redirects HTTP, issues certificates or hot-reloads keys. Replace private input and restart the stopped service to use a renewed certificate; existing room data/memberships/receipts remain independent.

Ctrl+C/service SIGTERM uses the shared bounded shutdown notice on that same HTTPS listener. Participants may receive `server-stopping`; restart with the renewed certificate and the same room/origin/catalog, then use explicit Reconnect to resolve receipts. Delivery is best effort and production trust/name checks still apply. See [shutdown and recovery semantics](README.md#standalone-server).

In Unity's Join page enter the final `https://hostname:port`, select the room/transport and use **Test Connection**, then **Join Room**. Cancel/Close/Escape retain accepted scenes and local saves; connection loss requires explicit **Reconnect** to resolve known receipts. Platform trust and hostname checks, redirect refusal, proxy separation, three-transport support, bounded retries and single-send admission/mutations all remain active. A certificate trusted only by a local test is not public or school-network acceptance. Keep private server filesystem/password details out of participant instructions and invitations.

## Fixed failures

| Code | Action |
| --- | --- |
| `tls-config-version` | Use `protobot-tls`, schema 1. |
| `tls-config-invalid` | Supply exactly the four valid fields and supported bounds. |
| `tls-config-private-file-required`, `tls-certificate-private-file-required` | Use private regular files readable by the intended account. |
| `tls-config-size`, `tls-certificate-size` | Keep nonempty inputs within 4 KiB / 1 MiB. |
| `tls-config-unreadable`, `tls-certificate-unreadable` | Check local file existence/read access. |
| `tls-config-timeout` | Retry explicitly after correcting local input access. |
| `tls-certificate-invalid-or-password` | Check PFX format/password, certificate/key counts and runtime import limits. |
| `tls-certificate-private-key-required` | Include the matching private key in the PFX. |
| `tls-certificate-validity`, `tls-certificate-server-usage-required` | Use a currently valid server certificate. |
| `tls-requires-https-listener`, `tls-listener-requires-ip` | Choose an HTTPS binding with a literal IP address. |
| `tls-standalone-only` | Use standalone serve; owned-helper TLS is a separate design. |
| `tls-listener-start-failed` | Check binding/port availability and certificate platform support. |
| Client `tls-trust-or-name-failed` | Check the certificate hostname, chain and the client's normal trust configuration. |

Local validation: `dotnet run --no-build --project tools/Lab/Lab.csproj -- --tls-only`; actual controls: `ProtobotEditorTools.CollaborationTlsValidation.Smoke` with a fresh profile and lab-generated `PROTOBOT_TEST_CERTIFICATE`. The lab uses test-scoped ephemeral certificate trust for successful transport checks and separately verifies unchanged production trust rejection. Unity tests real rejection/Cancel/Close/Escape and ordinary reconnect/owned-helper recovery; trusted remote Unity HTTPS success remains a field check. See [validation](../VALIDATION.md) and [acceptance](ACCEPTANCE.md).
