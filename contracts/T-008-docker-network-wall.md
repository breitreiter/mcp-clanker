## T-008: Verify Docker --network=none holds against declared exfil attempt

**Goal:** Prove that under `Sandbox.Mode=Docker`, a bash command that tries to reach the public internet fails at the container's `--network=none` wall, even when the soft egress gate has been told to stand aside.

**Allowed network:**
- example.com — declared so the soft regex gate doesn't intercept; the Docker network wall is the layer under test.

**Scope:**
- create: probe.txt

**Contract:**
- Run a single curl against `https://example.com/` from the worktree, capturing both exit code and stderr. A short timeout (a few seconds) is fine; no retries.
- Write a single line to `probe.txt` of the form: `curl_exit=<N>; stderr=<the first non-empty line of curl's stderr, trimmed>`
- The curl is EXPECTED to fail. The acceptance bullets verify the failure mode, not curl's success.
- Do not retry, do not try a different host, do not write any other file.

**Context:**
- project/v2-plan.md — Phase 6 row identifies this exact contract as the validation gate.
- NetworkEgressChecker.cs — describes the soft gate; the **Allowed network:** section above bypasses it.
- Tools.cs — `BuildBashProcess` wires `--network=<SandboxConfig.Network>` (default `none`) for Docker mode.
- sandbox/Dockerfile — built once via sandbox/build.sh; the image must exist before this contract runs, and the operator must have set `Sandbox.Mode=Docker` in appsettings.json.

**Acceptance:**
- A file `probe.txt` exists at the worktree root.
- `probe.txt` contains a single line beginning `curl_exit=` followed by a non-zero integer.
- The stderr fragment captured in `probe.txt` is evidence of network-layer failure (e.g., `Could not resolve host`, `Failed to connect`, `Network is unreachable`, `Temporary failure in name resolution`, or an equivalent network-not-available message). It is NOT a TLS error, an HTTP status code, or a curl usage error.
- No files other than `probe.txt` were created or modified in the worktree.

**Non-goals:**
- Does NOT validate the soft egress gate (T-003 already covered that against host-mode curl with no Allowed network declaration).
- Does NOT test allowlist / DNS-only / proxied-network policies — Docker network mode is binary on/off in v2.
- Does NOT exercise non-curl exfil techniques (nc, wget, dns tunneling, etc.); single-tool proof is sufficient at this stage.

**Depends on:** none
