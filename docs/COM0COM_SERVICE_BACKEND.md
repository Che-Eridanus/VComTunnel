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
bridges serial bytes in both directions.

Current scope:

- Requires `backend = com0comService`.
- Requires `visiblePort` and `backingPort` to name different com0com pair
  sides.
- Uses the existing com0com pair create/remove tooling.
- Does not require hub4com to be installed or detected.
- Supports RFC2217 initial negotiation, line/modem notification masks,
  startup serial/control status query, SIGNATURE response, remote
  FLOWCONTROL-SUSPEND/RESUME, idle NOP keep-alive, and service-level restart
  after transient network faults.

Known limitation:

- Dynamic local serial-control changes made by the application on the visible
  COM side are not yet fully observed and translated through the backing port.
  The next hardening step is to poll or subscribe to the backing-port state and
  map local baud, line-control, DTR/RTS, BREAK, purge, and XON/XOFF changes to
  the same RFC2217 commands that the KMDF backend already emits.
