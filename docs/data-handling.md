# Data handling (Hosted mode)

How the hosted, multi-tenant ClinicReport treats a clinic's data: where it is kept, how long, and who can reach it.

ClinicReport is designed with the principles of PHIPA in mind. It is not certified, audited or claimed to comply with
PHIPA or any other law. A clinic stays responsible for its own obligations, and this project takes no real patients or
payments: the demo data is synthetic and Stripe runs in test mode only.

## Where data is kept

| What | Where | Holds health information |
|---|---|---|
| A clinic's patients, reports, addenda, users and audit trail | Its own SQLite database, `clinicreport-{slug}.db` | Yes |
| Tenant catalog: name, status, plan, letterhead, the owner's email | `catalog.db` | No |
| Usage: counts of signed reports and active users per clinic | `catalog.db` | No, counts only |
| Operators and their security log | `catalog.db` | No |
| Card details and invoices | Stripe. ClinicReport keeps only its customer and subscription Ids | No |
| Data Protection keys (sessions and two-factor secrets) | `DataProtection__KeysDirectory` | No, but losing them signs everyone out and ends two-factor |

Clinics are apart by where their data is stored, not by a filter on a shared table. The host name picks the tenant, and
a session from one clinic is refused at another.

Stripe receives a clinic's slug, the owner's email and the number of seats. It receives nothing from a clinic's database.

Logs carry Ids and error types, not names, medical record numbers, report text or measurements.

## How long it is kept

| Event | What happens |
|---|---|
| Clinic is using the service | Everything is kept. Signed reports are frozen, and changes are appended to the audit trail |
| Payment fails | Past due, with a notice to everyone at the clinic and mail to the owner. Read-only after 7 days |
| Subscription ends | Read-only for 30 days, to export or subscribe again |
| After those 30 days | The subdomain stops answering and the data is still kept, until an operator deletes it |
| Deleted by an operator | The database file is gone, and the owner's email and letterhead with it. The catalog row stays, with `DeletedAt`, so the slug is never reused. Usage counts and the operator's log entry stay |
| Backups | A deleted clinic leaves the nightly backups as they age out (`KEEP_DAYS`, and the off-server retention) |

Deletion is never automatic. Clinical records can have legal retention periods that an automatic timer can't know
about, so a person decides, by typing the clinic's address, and only once the 30 days are over.

## Who can reach it

| Who | Can reach |
|---|---|
| People at the clinic | What their roles and services allow. Viewing, signing and restricted access are in the clinic's audit trail |
| The clinic's administrators | The above, plus a full export of the clinic's data, which is recorded in the audit trail |
| Operators, at `admin.{domain}` | The catalog, usage counts, and the size and last write time of each database file. The console has no way to open a clinic's database. Accounts are separate from every clinic, made only from the command line, and always use two-factor. Their actions are in the operator security log |
| Whoever administers the server | The files themselves. Nothing in the application stops someone with access to the disk, and the databases are not encrypted at rest by the application. Encryption at rest and server access are the host's to control |

## Taking data out

An administrator gets everything as one zip from the clinic's page (`GET /api/clinic/export`), also while suspended or in
the 30 days after cancelling: `patients.json`, `users.json`, `reports.json`, `audit.json`, the templates, and a PDF of
every signed report, made from the snapshot frozen at signing. Passwords and two-factor secrets are not included. The
file holds patient information, so where it goes after that is the clinic's responsibility.

## Backups and restoring one clinic

`dotnet ClinicReport.Api.dll --backup-all <folder>` copies `catalog.db` and each clinic's database with SQLite's backup
API, so a copy taken while the service runs is consistent. It exits with 1 if any clinic failed. Keep the Data Protection
keys with the backups.

To put one clinic back without touching the others:

1. Suspend it in the operator console, so that nobody is writing.
2. Run `dotnet ClinicReport.Api.dll --restore-tenant <slug> <folder>/clinicreport-<slug>.db`. The file is checked first,
   and a damaged file, or one that isn't a clinic's database, changes nothing.
3. Resume it. Anything the clinic wrote after the copy was taken is gone.
