# Running behind a reverse proxy

curto speaks plain HTTP. In production, put a reverse proxy with TLS in front of it (Caddy, nginx, Traefik, a cloud
load balancer). That works out of the box for the redirect and the dashboard, but three things need your attention,
because they depend on the address of the client and on the origin the visitor typed.

## 1. The client address

curto uses the caller's address for two things, both transient: the per-client **rate limits**, and the **visitor
hash** (see [privacy.md](privacy.md)). Behind a proxy that address is the proxy's, unless curto is told to read the
`X-Forwarded-For` header.

Measured with a redirect limit of 2 per minute and four requests that carried four different `X-Forwarded-For`
values:

| Setting | Status of the four requests | What it means |
|---|---|---|
| default | `302 302 429 429` | the header is ignored: every client shares one budget (the proxy's) |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` | `302 302 302 302` | each forwarded address has its own budget |

With the default, one busy visitor can exhaust the limit for everybody, and the daily visitor identifiers collapse
to "proxy address plus User-Agent". So behind a proxy, set:

```text
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
```

This is the standard ASP.NET Core switch. It makes the host honour `X-Forwarded-For` and `X-Forwarded-Proto`.

**Read this before turning it on.** The switch trusts those headers from **any** caller (it clears the default
"loopback proxies only" restriction), and curto has no setting for a list of trusted proxies. The proof is the same
experiment: with the switch on, rotating the header value gets around the rate limit. So the switch is only safe when
nothing but the proxy can reach curto's port:

- bind curto to loopback (`ASPNETCORE_URLS=http://127.0.0.1:8080`) when the proxy runs on the same host, or
- keep the container on a private network that only the proxy joins, and do not publish its port, or
- publish it on `127.0.0.1` only, which is what the supplied `docker-compose.yml` does.

Never expose a port with the switch on directly to the internet.

## 2. The public origin

Short URLs, and the QR codes that encode them, are built from `CURTO_BASE_URL`. If it is empty curto falls back to
the `Host` header of the request, which a proxy may rewrite and a client may fake. Set it to the public origin
(scheme and host, no path):

```text
CURTO_BASE_URL=https://s.example.com
```

Also consider restricting `AllowedHosts` (the standard host filter, `*` by default) to that host name.

## 3. Streaming

The live view uses a long-lived Server-Sent Events response. curto sends `Cache-Control: no-store`,
`X-Accel-Buffering: no` and a heartbeat comment every 15 seconds (`SseHeartbeatSeconds`), which is enough for most
proxies. Make sure the proxy does not buffer the response and that its idle or read timeout is longer than the
heartbeat interval. If the stream keeps reconnecting, the proxy is the first suspect: the dashboard backs off from 1
to 30 seconds and shows the reason in the "Live" indicator.

## Example configurations

These snippets are the usual shape of such a setup. They were not run as part of this project's tests, so check them
against your proxy's current documentation.

Caddy sets `X-Forwarded-For`, `X-Forwarded-Proto` and `Host` itself and turns on automatic HTTPS:

```text
s.example.com {
    reverse_proxy 127.0.0.1:8080 {
        flush_interval -1
    }
}
```

nginx needs the headers spelled out, and buffering switched off for the stream:

```nginx
server {
    server_name s.example.com;
    # listen 443 ssl; certificates omitted

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;
        proxy_read_timeout 1h;
    }
}
```

Terminate TLS at the proxy and keep the hop to curto on loopback or a private network. curto sends
`Strict-Transport-Security` itself whenever it sees an HTTPS request (after `X-Forwarded-Proto` is applied).
