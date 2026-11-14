// The device channel (T-244): a SignalR JSON hub protocol endpoint, server to client only, so the authenticator needs no Firebase.
//   POST /devices/hub-ticket (in app.ts)  -> { ticket, expires_in: 30 }, single use
//   GET  /hubs/device?access_token=<ticket> (WebSocket)
// Framing: every message is JSON followed by the record separator 0x1e; a frame may carry several messages.
//   client -> {"protocol":"json","version":1}<RS>   server -> {}<RS>   (the handshake, once)
//   server -> {"type":1,"target":"<event>","arguments":[{...}]}<RS>    (invocation; payloads are ids only)
//   either -> {"type":6}<RS>                                           (ping; the server answers a ping with a ping and sends one on a timer)
//   server -> {"type":7,"allowReconnect":true|false}<RS>               (close)
import { randomBytes } from "node:crypto";
import type { IncomingMessage, Server } from "node:http";
import type { Duplex } from "node:stream";
import { WebSocketServer, type WebSocket } from "ws";

export const HUB_TICKET_SECONDS = 30;
export const HUB_DEFAULT_LIFETIME_SECONDS = 30 * 60;
export const HUB_DEFAULT_PING_SECONDS = 15;
export const HUB_PATH = "/hubs/device";
const RS = "\u001e";

export type HubEvent = "consent_request" | "consent_closed" | "device_enrolled" | "settings_changed";

export interface DeviceHubOptions {
  /** Unix seconds (the mock's clock): ticket expiry. */
  now: () => number;
  /** A connection is closed (allowReconnect true) after this many seconds. Default 30 minutes. */
  lifetimeSeconds?: number;
  /** Seconds between server pings. Default 15. */
  pingSeconds?: number;
}

interface Conn { ws: WebSocket; sub: string; kid: string; shaken: boolean; timers: NodeJS.Timeout[] }

export function createDeviceHub(options: DeviceHubOptions) {
  const lifetimeMs = (options.lifetimeSeconds ?? HUB_DEFAULT_LIFETIME_SECONDS) * 1000;
  const pingMs = (options.pingSeconds ?? HUB_DEFAULT_PING_SECONDS) * 1000;
  const tickets = new Map<string, { sub: string; kid: string; expires: number }>();
  const conns = new Set<Conn>();
  const wss = new WebSocketServer({ noServer: true, maxPayload: 4096 });

  function issueTicket(sub: string, kid: string) {
    for (const [t, v] of tickets) if (v.expires <= options.now()) tickets.delete(t);
    const ticket = randomBytes(24).toString("base64url");
    tickets.set(ticket, { sub, kid, expires: options.now() + HUB_TICKET_SECONDS });
    return { ticket, expires_in: HUB_TICKET_SECONDS };
  }

  const send = (c: Conn, ...messages: object[]) => {
    if (c.ws.readyState === c.ws.OPEN) c.ws.send(messages.map((m) => JSON.stringify(m) + RS).join(""));
  };

  function closeConn(c: Conn, allowReconnect: boolean) {
    send(c, { type: 7, allowReconnect });
    c.ws.close(1000);
  }

  function onUpgrade(req: IncomingMessage, socket: Duplex, head: Buffer) {
    const url = new URL(req.url ?? "/", "http://mock");
    if (url.pathname !== HUB_PATH) return false;
    const ticket = url.searchParams.get("access_token") ?? "";
    const t = tickets.get(ticket);
    tickets.delete(ticket); // single use, even when it turns out to be expired
    if (!t || t.expires <= options.now()) {
      socket.end("HTTP/1.1 401 Unauthorized\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
      return true;
    }
    wss.handleUpgrade(req, socket, head, (ws) => {
      const c: Conn = { ws, sub: t.sub, kid: t.kid, shaken: false, timers: [] };
      conns.add(c);
      c.timers.push(setTimeout(() => closeConn(c, true), lifetimeMs), setInterval(() => c.shaken && send(c, { type: 6 }), pingMs));
      ws.on("message", (data) => {
        const text = data.toString("utf8");
        for (const part of text.split(RS)) {
          if (!part) continue;
          let m: any;
          try {
            m = JSON.parse(part);
          } catch {
            return void ws.close(1007);
          }
          if (!c.shaken) {
            if (m?.protocol !== "json" || m?.version !== 1) {
              ws.send(JSON.stringify({ error: "only the json protocol, version 1, is supported" }) + RS);
              return void ws.close(1002);
            }
            c.shaken = true;
            ws.send("{}" + RS);
          } else if (m?.type === 6) send(c, { type: 6 });
          // nothing else is accepted from the client: invocations are server to client only
        }
      });
      ws.on("close", () => {
        conns.delete(c);
        for (const x of c.timers) clearTimeout(x), clearInterval(x);
      });
      ws.on("error", () => ws.terminate());
    });
    return true;
  }

  return {
    issueTicket,
    /** Wires the upgrade handler into the mock's HTTP server and tears the hub down with it. */
    attach(server: Server) {
      server.on("upgrade", (req, socket, head) => {
        if (!onUpgrade(req, socket, head)) socket.end("HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
      });
      server.on("close", () => this.close());
    },
    /** Delivers an event (ids only) to every connected device of the user. */
    notify(sub: string, target: HubEvent, payload: Record<string, string>) {
      for (const c of conns) if (c.sub === sub && c.shaken) send(c, { type: 1, target, arguments: [payload] });
    },
    /** Closes the connections of one device, e.g. when it is cancelled (allowReconnect false). */
    closeDevice(kid: string, allowReconnect: boolean) {
      for (const c of [...conns]) if (c.kid === kid) closeConn(c, allowReconnect);
    },
    connections: () => conns.size,
    close() {
      for (const c of [...conns]) c.ws.terminate();
      wss.close();
    },
  };
}
