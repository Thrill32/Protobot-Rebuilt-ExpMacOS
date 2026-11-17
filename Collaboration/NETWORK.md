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

## Admission feedback and bounded safe retries

The policy is identical for System, Direct and explicit HTTP-proxy routes. Safe retries reuse the same private input, handler and origin; they never change routes. TLS/name failure, redirects, 401/403/407, wrong passwords, revoked membership, incompatible versions/catalogs and malformed responses are terminal. Bare HTTP 429/503 or arbitrary proxy bodies do not authorize a retry. Only recognized request/transfer congestion codes or a network interruption/timeout at an explicitly safe call site do.

| Operation | Automatic behavior within one explicit attempt |
| --- | --- |
| Capability/health reads; explicit CLI participant/admin lists and diagnostic GETs | At most **3 sends per read**, **12 seconds total**, **4 seconds per request**, one request at a time. |
| Immutable geometry GET/HEAD/PUT | At most **3 sends per object operation**, **120 seconds total**, one transfer per client. Failed partial bytes are discarded. A PUT may be repeated because its validated content address is immutable. |
| Join/resume; commits, personal history, leases/previews, Leave, room creation and participant removal | **One send**. A definitive rejection permits a later manual retry; a lost reply is ambiguous. Resolve known membership/pending receipts through explicit reconnect or inspect administration state. Never replay a command automatically. |
| WebSocket setup and held/short event reads | Existing Auto WebSocket → held-request fallback only. Event loss freezes the connection; **no retry loop or background reconnect**. Short polling retains one read and its **250 ms active / 1,000 ms idle** minimum delays. |

Between safe sends, exponential delay is 250 ms then 500 ms, plus independently sampled **0–250 ms jitter**. A recognized busy response can increase the wait using a single integer `Retry-After` delta-seconds hint; automatic waits clamp to **250–5,000 ms**. Date, negative, duplicate, malformed or overlong values use the fixed local default; numeric extremes clamp. Hints never increase attempts/deadlines and never authorize mutation replay. Responses/streams are disposed before waiting. Cancellation interrupts requests/transfers/waits without another send.

A shared-client Join/Resume/Reconnect attempt has a **three-minute overall deadline**, including all preflight reads, model transfers and receipt inspection. An already-sent admission still settles its reply for up to ten seconds before cancellation/deadline is honored and returned credentials are retained; this can extend the overall ceiling by at most ten seconds. Owned-helper startup keeps its separate thirty-second bound. Accepted scenes, local file associations/bytes/saved state, original origin/room/catalog/generation scope, known credentials and unresolved IDs survive failed/canceled attempts.

New helpers supply fixed rejection codes and standard `Retry-After` guidance for `server-request-limit`, `room-request-limit`, `asset-transfer-busy`, `server-transfer-busy` (one second), `room-full` (ten seconds), and `join-rate-limit` (remaining room window, rounded up). Request permits are checked before endpoint work; join rate/full checks precede membership creation; transfer gates precede immutable publication. Guidance does not reserve a slot or promise availability. Existing limits remain: **60 admission requests per room per minute**, **10 active / 1,000 retained memberships**, **32 requests per room / 64 per process**, **one transfer per room / two per process**. Rejected rate attempts saturate the per-room counter; no IP/identity queues/maps or forwarded-address trust are added. Other rooms keep their own request/transfer gates. `HEAD` errors add an allowlisted `X-Protobot-Rejection` header because HEAD carries no body; older helpers retain their existing bounded fallback.

Unity shows waiting attempt/reason, exhausted attempts and a countdown for definitive admission rejection. Join/Reconnect is disabled for that address/room during its bounded **250–60,000 ms** manual cooldown; changing room/address is independent. Cancel keeps the window; Close/Escape returns to editing. CLI doctor/read commands print fixed waiting/exhausted codes, Ctrl+C cancels, and doctor reports manual cooldowns. After failed/ambiguous admission, doctor stops further transport admissions. An older helper needs no new capability: absent hints use fixed defaults. HTTP headers are additive; protocol remains **0.9**, data/archive **v2**, config/registry/admin and diagnostic exports **v1**, local network config **schema 1**. No wire/storage migration or new persisted retry configuration is needed.

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
