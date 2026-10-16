## Context

`LoopbackAuthenticationHandler` grants `Operator` and `LocalProcess` to each request whose source address is loopback in `local` mode. A browser on the same host can send such a request. DNS rebinding gives the attacker page a foreign `Host` name. A cross-site form or fetch gives a foreign `Origin`. A probe at `dev` confirmed both: `/api/health/status` returned 200, and a foreign-origin WebSocket upgrade to `/hub/session` returned 101.

## Goals / Non-Goals

**Goals:**

- Refuse loopback-source requests that a foreign web page can cause.
- Keep the CLI, the TUI, and a browser on `localhost` working with no new prompt.

**Non-Goals:**

- Bearer requests, anonymous routes, and tunnel or `reverse-proxy` modes. A page cannot hold a device token, and a reverse proxy sends the public `Host` name.
- A per-session CSRF token. Two headers already show the threat.

## Decisions

- **Put the check in `LoopbackAuthenticationHandler.HandleAuthenticateAsync`.** It runs after the exposure-mode guard and the loopback address test, and before the claims. No route can skip it there. Alternative: the ASP.NET `AllowedHosts` filter or a middleware. Both cover bearer and anonymous routes, and the host filter does not test `Origin`.
- **Keep the rule in one static policy type.** `LoopbackHostOriginPolicy` takes the `HostString` and the `Origin` values and returns a refusal reason or none. The unit tests and the mutation gate call it directly.
- **Fixed rule, no knob.** A knob would copy data that the request already holds.
- **Return `AuthenticateResult.Fail`.** A failed result leaves the user anonymous. A protected route then challenges and answers 401. An anonymous route still answers.
- **Compare the Origin to `http://` plus the Host.** The daemon serves plain HTTP in `local` mode, so an `https://` Origin never matches.

## Risks / Trade-offs

- A client that uses a custom host name for the loopback daemon gets 401 → the release note and the skill entry say so.
- A refusal can log once for each anonymous request that has a foreign `Host` → the log shows the two header values only.
