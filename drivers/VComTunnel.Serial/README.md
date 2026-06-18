# VComTunnel.Serial KMDF Prototype

This directory contains the phase 2 driver design. It is not a working driver yet:
there is no `VComTunnel.Serial.sys`, no catalog, and no WDK project in this tree.

The intended phase 2 result is a single visible COM device backed by
`VComTunnel.Service`, replacing the phase 1 `com0com + hub4com` chain for one
mapping at a time.

Read these documents before writing driver code:

- [DESIGN.md](DESIGN.md) - driver architecture, queues, serial behavior, risks
- [SERVICE_CHANNEL.md](SERVICE_CHANNEL.md) - private service/driver protocol
- [SERVICE_BACKEND.md](SERVICE_BACKEND.md) - user-mode backend and acceptance gate

Current status:

- The `.NET` service and GUI still treat `kmdf` mappings as unsupported.
- `VComTunnel.Serial.inf` is a more complete INF skeleton, but it is still a
  scaffold until a signed `.sys` and `.cat` exist.
- `install-test-driver.ps1` refuses to install unless those package files exist.

Implementation entry point:

1. Create a WDK KMDF driver project named `VComTunnel.Serial`.
2. Implement a root-enumerated Ports-class device that publishes one COM name.
3. Implement the private service channel described in `SERVICE_CHANNEL.md`.
4. Implement the serial IOCTL/read/write subset from `DESIGN.md`.
5. Add the service-side backend described in `SERVICE_BACKEND.md`.
6. Add fake-driver tests before real RFC2217.
7. Only then generate a test-signed package and install it on a disposable or
   backed-up Windows 10/11 x64 machine.
