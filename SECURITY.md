# Security

Please report vulnerabilities privately through GitHub's
[security advisories](https://github.com/ArashKeshtgar/ClinicReport/security/advisories/new) rather than a
public issue. Include steps to reproduce; do not include real patient data.

Deployment notes:

- Serve over HTTPS only, and set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` behind a reverse proxy so the
  session cookie is marked `Secure`.
- Never enable `Demo__Enabled` on an instance that holds real patients: it adds one-click demo sign-in.
- The SQLite file in `/data` contains patient data. Encrypt the disk it lives on and its backups.
- Sessions use an HttpOnly, SameSite=Strict cookie; state-changing API calls also require the
  `X-ClinicReport` header. Deactivating a user or changing their role ends their session on the next request.
