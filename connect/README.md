# Lensio Connect

A separate Cloudflare Worker and SQLite-backed Durable Object namespace provide short-lived device discovery and WebRTC signaling. No media relay, account, KV or D1 database is used. The deployment is independent from other Workers in the same Cloudflare account. The account's Workers and Durable Objects usage limits are shared; do not enable a paid plan for this feature. The free limits are finite, so public demand can exhaust them. See Cloudflare's current pricing before changing account plans.

The web client uses `https://lensio-connect.pex-am.workers.dev`. The service accepts WebSockets from the Lensio GitHub Pages origin or the Windows loopback receiver origin. Origin validation limits normal browser callers; it is not authentication against arbitrary non-browser clients.

The current native QR receiver uses a private pairing room: the QR contains SHA-256 of the PC private key, while only the PC sends the private receiver proof. The Worker verifies the proof before allowing the host role. Up to four phone sockets can pair independently to one host. Legacy discovery without a QR still groups devices using a server-keyed HMAC of the client public IPv4 address or IPv6 /64 prefix. Raw addresses are not returned to peers, logged by application code, or persisted by this Worker. Cloudflare necessarily processes connection metadata under its own policies. No raw IP, SDP or device-name records are written to Durable Object storage. WebSocket attachments contain ephemeral role, name and paired session identity and may be held by the platform during hibernation. Disconnects clear pair ownership; idle sessions expire after 90 seconds and are swept every 60 seconds.

Sharing a public address or prefix is **not proof of sharing a router or identity**. Carriers and ISPs can place unrelated customers behind one address; VPNs, iCloud Private Relay, guest networks, IPv4/IPv6 differences and network isolation can hide actual nearby devices. The QR workflow instead identifies the PC through the private pairing room and receiver proof. After the user scans that PC and presses Start camera, signaling is automatic. Legacy network-only pages still require choosing and approving the receiver. Never approve an unfamiliar receiver. There is no automatic trust of previously named devices. The page keeps manual pairing as a fallback.

Each room admits up to 32 sockets, accepts only bounded JSON text messages, rate-limits each socket, validates SDP types/sizes, restricts signaling to a paired host/phone, rejects a second host claiming a busy phone and releases ownership on cancellation or socket close. The random per-socket ID is scoped to that live room. QR room and receiver proof parameters are sent only to the HTTPS/WSS service; treat them as credentials and keep them out of logs and published screenshots. These bounds do not replace account-wide abuse protection or a production capacity review before a large launch. A 25-second heartbeat keeps active sessions alive; messages contain no frames.

## Deploy

Use the official Wrangler CLI and authenticate interactively. Keep `DISCOVERY_SALT` in Cloudflare secrets and never commit it, Cloudflare tokens, account IDs, Wrangler caches, or private logs. The checked-in config declares a dedicated `lensio-connect` Worker and `NETWORKS` namespace. Do not reuse another project's name or resources.

```
npx wrangler deploy --config connect/wrangler.jsonc
npx wrangler secret put DISCOVERY_SALT --config connect/wrangler.jsonc
```

Generate a long random salt locally and pass it over stdin or the CLI's hidden prompt. Update `web/config.json` and the exact WSS origin in `WebCameraBridge.cs` if the deployment URL changes. Rebuild the Windows EXE so embedded assets match the public client. A changed salt temporarily separates old and new discovery rooms until clients reconnect.

Run `node tests/discovery.test.mjs` for offline role/ownership, cleanup, size/rate bounds, Origin validation and IPv6 grouping checks. For a live network smoke test, install `ws` locally with `npm install --prefix connect --no-save --package-lock=false --ignore-scripts ws`, then run `node tests/discovery-live.test.mjs`. The live test uses synthetic names and SDP only and does not access a camera.
