---
name: Bug report
about: Report a reproducible VComTunnel problem
title: "[Bug] "
labels: bug
assignees: ""
---

## Summary

Describe the problem and the expected behavior.

## Environment

- Windows version:
- VComTunnel commit or release:
- Backend: `com0comHub4com`, `com0comService`, or `kmdf`
- Visible port:
- Backing port, if applicable:
- RFC2217 endpoint type:

## Reproduction Steps

1.
2.
3.

## Observed Result

Include relevant GUI messages, `vcomtunnelctl logs`, service logs, or smoke-test
output.

## Expected Result

Describe what should have happened.

## Hardware and Control-Line Risk

State whether DTR, RTS, BREAK, purge, baud-rate changes, or reconnect behavior
could reset or disturb attached hardware.

## Validation Already Tried

- `vcomtunnelctl diagnose`:
- Local fake-server smoke:
- RFC2217 probe:
- Real-device validation:
