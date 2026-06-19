## Summary

Describe what changed and why.

## Backend Scope

- [ ] GUI only
- [ ] CLI only
- [ ] Service/core
- [ ] `com0comHub4com`
- [ ] `com0comService`
- [ ] `kmdf`
- [ ] Documentation or packaging only

## Validation

- [ ] `dotnet build VComTunnel.sln`
- [ ] `dotnet run --no-build --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj`
- [ ] `scripts\smoke-local.ps1`
- [ ] Fake-server RFC2217 probe
- [ ] Real RFC2217 endpoint
- [ ] Real hardware
- [ ] Not run; explain below

## Risk Review

Note any effects on COM port allocation, service lifecycle, driver install,
DTR/RTS/BREAK/purge behavior, attached hardware, or third-party dependency
packaging.
