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
  `X-ClinicReport` header. The cookie holds only the user id and a security stamp; roles, permissions and
  services are re-read on every request, so changes apply at once, and a new stamp (password change or
  reset, deactivation, two-factor reset, "sign out everywhere") ends existing sessions.
- Passwords are hashed with ASP.NET Core Identity's PBKDF2 hasher. Five failed sign-ins lock an account
  for 15 minutes (an admin can unlock it); sign-in is also rate-limited per IP address.
- Two-factor sign-in uses standard TOTP (RFC 6238); secrets are encrypted with ASP.NET Data Protection,
  whose keys live in the data volume (`/data/keys`), and each code is accepted only once.
- The security log (Audit log page) records sign-ins, failures, lockouts, account and role changes,
  report signing, and report views (one entry per user and report per 10 minutes). Rows are never edited.
