# Outbound proxy configuration

This is local client configuration, independent of room passwords, participant credentials and owner/admin capabilities. Wire stays **0.9.0-alpha**, room/SQLite/archive formats **v2**, and server configuration/registry/admin **v1**. No server capability or storage migration is required.

## Unity controls

The collaboration window offers **System proxy settings (default)**, **Direct connection**, and **Explicit HTTP proxy**. Enter an explicit `http://host:port` authority and choose **None** or **Basic** authentication. Username/password use separate masked fields. Input stays in this process's memory: no preferences, recovery/bootstrap files, invites, logs, diagnostics or additional test windows receive it. Re-enter it after application restart. **Clear proxy input** clears the form for subsequent connections; a running connection retains its policy until paused/reconnected or closed.

On a remote session choose **Pause connection to change network route**, edit the route, then **Reconnect**. The same origin/room/catalog/generation membership, personal history and pending receipt IDs are retained. Failure/cancellation keeps accepted content and local files. A replacement generation is refused before admission. There is no implicit direct bypass, automatic reconnect, mutation replay or new membership on route change. Owned-helper loopback Host/Resume/restart and inspection always use Direct. `--collab-network-route system|direct` selects the two nonsecret launch modes; explicit proxy details are window input.

## CLI private file

Remote commands accept either `--network-route system|direct` or `--network-file /private/local/network.json`. The file is **format `protobot-network`, schema 1**, at most **4 KiB**. A symlink at the file itself is refused. Unix files must exclude group/other access; Windows requires the current operator's ownership/ACL. Create the file in a private directory with an editor and protect it before use. Never put proxy credentials in shell commands, environment variables or process arguments. Replace these placeholders only inside that private file:

```json
{
  "format": "protobot-network",
  "schema": 1,
  "route": "http-proxy",
  "proxy": "http://proxy.example.org:3128",
  "authentication": "basic",
  "username": "ENTER PRIVATELY",
  "password": "ENTER PRIVATELY"
}
```

Anonymous proxies use `"authentication": "none"` and omit username/password. System/Direct file configurations use `"authentication": "none"` and omit proxy/credentials. Basic input is 1–256 printable ASCII characters per field, without a colon in the username.

```sh
protobot-collab doctor --url https://cad.example.org --room team --catalog ./catalog.json --network-file ./private/network.json --quick
protobot-collab diagnostics --url https://cad.example.org --owner-file ./private/owner.json --network-file ./private/network.json
protobot-collab room list --url https://cad.example.org --admin-file ./private/admin.json --network-file ./private/network.json
protobot-collab doctor --url https://cad.example.org --network-route direct
```

The options also apply to `participants`, `remove-participant`, `room create` and server-admin diagnostics. `serve`, init and offline backup/restore/registry restoration have no outbound route configuration. Config loading is asynchronous, cancellable and byte bounded. Each command/client owns its handler; no global/OS/environment settings or other processes/profiles are changed.

## Mechanisms and limits

HTTP origins use forward requests; HTTPS origins use CONNECT through the selected **HTTP** proxy. Platform certificate and hostname validation remain mandatory. **Basic is unencrypted on the HTTP hop to the proxy**; use it only on a trusted proxy network. Origin TLS does not encrypt first-hop proxy authentication. Credentials are offered only for that proxy's Basic challenge; origin 401 challenges cannot obtain them. CONNECT does not carry origin bearer/owner/admin credentials. A final 407 is rejected before reading its body, including reflected/encoded credentials and oversized errors.

Capability/health checks, admission, events on all three transports, assets, leases/previews, edits/history, receipts, leave, doctor, diagnostics and protected CLI owner/admin operations all follow the policy. WebSocket upgrades use the same redirect-disabled HTTP handler, with runtime WebSocket framing. HTTP/upgrade redirects are refused. Auto retains WebSocket → held requests. Existing request/connection/transfer bounds and cancellation apply; an already-sent admission settles within its existing ten-second bound to retain any returned membership.

Unsupported explicit mechanisms: proxy userinfo/paths/queries/fragments, HTTPS-to-proxy, SOCKS, PAC, Digest, NTLM, Kerberos and Negotiate. System leaves existing runtime/OS proxy/trust behavior intact; it supplies no new PAC/integrated-auth implementation. Certificate provisioning, custom PAC, SOCKS and new integrated authentication remain separate work.

| Fixed code | Action |
| --- | --- |
| `proxy-config-version` | Use format `protobot-network`, schema 1. |
| `proxy-route-unsupported`, `proxy-auth-unsupported`, `proxy-auth-route-mismatch` | Select a supported mode/authentication pair. |
| `proxy-address-invalid`, `proxy-credentials-invalid` | Correct the authority/private Basic input using the constraints above. |
| `proxy-config-private-file-required` | Use a regular private credential file owned/readable by the operator. |
| `proxy-config-size`, `proxy-config-unreadable` | Use a readable local JSON file of 1–4096 bytes. |
| `proxy-authentication-required` | Check Basic credentials and supported challenges with the network operator, then reconnect. |

DNS, refused/unreachable network, TLS and timeout diagnostics retain fixed codes. Error text never includes an address/configuration path or arbitrary proxy response. Diagnostic schema 1 adds only allowlisted `networkRoute` (`system`, `direct`, `http-proxy`) and `proxyAuthentication` (`none`, `basic`); no proxy secrets or configuration paths are exported.

Local automation targets pinned SDK 10.0.403 (.NET runtime 10.0.14) and Unity 6000.6.6f1 on Mac. Windows/Linux execution, Mac↔Windows editing/transfers/cancellation/focus, native file panels, cross-OS archives, Debian/Pi service/resource behavior and trusted public HTTPS/proxy/school-network acceptance remain pending. A loopback proxy and an untrusted TLS fixture do not establish those field results.
