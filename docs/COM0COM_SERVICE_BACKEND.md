# com0comService Backend

`com0comService` is the transitional backend between the phase 1
`com0comHub4com` bridge and the KMDF backend.

Data path:

```text
serial tool -> visible COMx -> com0com -> backing CNCBx
  -> VComTunnel.Service -> RFC2217 host:port
```

This backend still uses com0com for the Windows-visible virtual COM pair, but
it does not start `hub4com` or `com2tcp-rfc2217.bat`. `VComTunnel.Service`
opens the backing port directly, connects to the RFC2217 endpoint, performs the
same Telnet/RFC2217 startup negotiation used by the KMDF service path, and
bridges serial bytes in both directions. The local backing port is opened with
Win32 overlapped I/O so serial RX/TX and modem-status events can progress as
separate pipeline stages instead of depending on synchronous polling.

Current scope:

- New RFC2217 mappings default to the standard TCP port `2217`; an explicitly
  configured or device-advertised custom port remains unchanged.
- Requires `backend = com0comService`.
- Requires `visiblePort` and `backingPort` to name different com0com pair
  sides.
- Exposes fixed service-side com0com pair create/remove APIs so product UIs do
  not need to run `setupc.exe` with a UAC prompt for every add/delete action.
  Driver installation or repair remains an explicit elevated setup step.
- Does not require hub4com to be installed or detected.
- Supports RFC2217 initial negotiation, line/modem notification masks,
  startup serial/control status query, SIGNATURE response, and an advisory
  `VComTunnel` SIGNATURE identity for an always-on background mapping. The
  identity lets XC-WSER distinguish that mapping from a foreground `idf.py`
  session but never grants UART ownership. Also supports remote
  FLOWCONTROL-SUSPEND/RESUME, idle NOP keep-alive, OS TCP keepalive configured
  as 5 seconds idle / 1 second interval / 3 failed probes, and service-level
  restart after transient network faults. Keepalive only detects a dead peer;
  it does not change user-controlled connect, reset, DTR, or RTS behavior.
- Observes local backing-port CTS/DSR events on the primary serial handle with
  overlapped `WaitCommEvent` and maps the com0com peer state to RFC2217
  DTR/RTS changes, matching the `hub4com` `pinmap` direction for explicit
  control-line forwarding.
- Forwards explicit DTR/RTS changes 1:1 and in arrival order. com0com also
  reports an ambiguous both-lines-on snapshot whenever an application opens
  the visible COM port. The service defers only that initial snapshot until
  the next observed state: an immediate both-off attach cycle is discarded so
  `idf.py monitor --no-reset` cannot reset the target, while a one-line change
  commits the deferred levels before forwarding an intentional reset sequence.
- Read-only background logging opens (Exclusive mode) clear the stored
  DTR/RTS control bits before the first SetCommState, so a logging session
  never re-asserts lines left behind by a previous serial tool; the
  configured DTR/RTS policy is applied explicitly afterwards.
- Configures the service-owned backing `CNCB` handle as an 8-bit binary byte
  transport. com0com ports can retain a legacy `7E1` DCB from their default or
  previous opener; inheriting that DCB clears payload bit 7 before the service
  can forward the byte (`0xC0` becomes `0x40`). This internal DCB does not
  override the application's serial format: visible-side baud/data/parity/stop
  changes are received through com0com insertion events and forwarded as
  RFC2217 settings to the remote physical UART.
- Writes RFC2217 RX data to the local COM side through a bounded small-chunk
  pipeline so the TCP reader is not blocked by normal local COM write latency.
- Keeps com0com receive-overrun emulation disabled for lossless active-session
  backpressure, but treats each visible-peer baud insertion as a new local COM
  receive generation. The service aborts the backing-port pending write and
  rejects older queued generations before forwarding the new session, so logs
  received while the visible COM was closed cannot be replayed into esptool or
  another later serial client.
- Removes stale runtime entries when a saved mapping is deleted, so `/api/status`
  does not keep advertising a COM mapping that no longer exists in config.

Known limitation:

- BREAK, purge, and XON/XOFF actions from arbitrary Windows serial tools are
  still not all surfaced through this backend with the same fidelity as
  hub4com's full filter graph.
