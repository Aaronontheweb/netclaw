## 1. Probe and tests

- [ ] 1.1 Run the probe at `dev` and record the result
- [ ] 1.2 Add handler tests for the Host rule, the Origin rule, and the non-loopback and reverse-proxy controls
- [ ] 1.3 Add hub upgrade and lifecycle shutdown tests with a foreign Origin

## 2. Implementation

- [ ] 2.1 Add `LoopbackHostOriginPolicy` and call it from `LoopbackAuthenticationHandler`
- [ ] 2.2 Log each refusal at warning level with the Host and Origin values

## 3. Gates and docs

- [ ] 3.1 Add the focused mutation gate script, its tests, the CI matrix entry, and the `TOOLING.md` entry
- [ ] 3.2 Update the `netclaw-operations` system skill and bump its version
