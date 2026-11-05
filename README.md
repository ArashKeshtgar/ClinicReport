# ClinicReport

**Open-source structured reporting for clinics.** Each service the clinic reports on (echocardiography,
ECG, Holter, ultrasound…) gets a *template* that an administrator designs in the browser. During the
exam, a typist fills the report on that template while the physician dictates; the physician sees the same
report live, writes the conclusion and signs it.

The template does the writing: every answer carries the sentence it contributes, so clicking
*Mitral valve → Abnormal → thickened, calcified* produces
“The mitral valve shows thickened leaflets and calcified leaflets.” Measurements are checked against
sex-specific normal ranges and flagged, derived values (BSA, LV mass index, E/e′, RVSP, QTc…) are
calculated, and abnormal findings can be pulled into the conclusion with one click.

> **Not a medical device.** ClinicReport is provided as-is under the MIT license. The bundled templates and
> reference ranges are a starting point drawn from published guidelines; every clinic must review and
> validate templates, ranges and generated text before using them with patients. See [Disclaimer](#disclaimer).

## Features

- **Template designer** – sections, single/multiple choice, numbers with units and normal ranges, calculated
  fields, free text, follow-up questions that appear only for certain answers, and an *All normal* preset per
  section. Visual editor with a live “try it” preview, plus a JSON view. Import/export templates as JSON.
- **Versioned templates** – publishing freezes a version. Reports always render with the version they were
  started on, so editing a template never changes an old report.
- **Live co-editing** – typist and physician work on the same report at once (SignalR). Each person sees who
  else is in the report and which field they are typing in; concurrent edits to different fields never
  overwrite each other.
- **Narrative + measurements** – findings paragraph per section, measurement table with H/L flags, computed
  values, and a conclusion draft from the abnormal findings.
- **Sign-off** – only physicians sign. Required fields and a conclusion are enforced. Signing freezes the
  rendered text; later corrections are signed addenda.
- **Users, roles and permissions** – roles are built from eight permissions (view, start, fill or sign
  reports; manage patients; design templates; manage users; view the audit log), users can hold several
  roles, and each user can be limited to some services (e.g. a typist who only sees echocardiography).
  Built-in Administrator, Physician and Typist roles; add your own.
- **Account security** – temporary passwords that must be changed at first sign-in, lockout after five
  failed attempts, two-factor sign-in with any authenticator app (can be required per role), "sign out
  everywhere", and immediate effect when an admin deactivates a user or changes their roles.
- **Audit trail** – every field change is recorded with user and time, and a separate security log
  records sign-ins, account and role changes, report signing, and who opened which patient's report.
- **Print / PDF** – A4 print layout with clinic header and electronic signature line.
- **Self-hosted, small** – one Docker container, SQLite database in a volume. No external services.

### Bundled templates

| Template | Contents |
| --- | --- |
| [`echo-tte.json`](templates/echo-tte.json) | Adult transthoracic echo: LV/RV size and function, RWMA, diastolic grade, atria and septum, four valves with stenosis/regurgitation grading and prostheses, pericardium, aorta, IVC; 20 measurements with ASE/EACVI 2015 ranges; BSA, LV mass (index), RWT, LAVI, E/A, E/e′, RVSP. |
| [`ecg-12-lead.json`](templates/ecg-12-lead.json) | Resting ECG: rhythm, rate, intervals, QTc (Bazett and Fridericia), axis, conduction, chamber enlargement, ST-T and Q waves, comparison. |

New services need no code: design a template, publish it, start reporting. Contributions of templates for
other services are very welcome (see [CONTRIBUTING.md](CONTRIBUTING.md)).

## Quick start (Docker)

```bash
docker compose up -d
docker compose logs clinicreport   # shows the generated admin password on first start
```

Open http://localhost:8080 and sign in as `admin` with that password; you are asked to choose your own
right away. Then add people under **Users & roles**: give each one a role (or several), limit them to some
services if needed, and hand them the temporary password. Physicians' credentials are printed under the
signature. Consider requiring two-factor sign-in for the Physician and Administrator roles.

Configuration is by environment variable (see [`docker-compose.yml`](docker-compose.yml)):

| Variable | Purpose |
| --- | --- |
| `Clinic__Name`, `Clinic__Address`, `Clinic__Phone`, `Clinic__ReportFooter` | Printed on reports |
| `Bootstrap__AdminPassword` | Password for the first admin instead of a generated one |
| `ConnectionStrings__ClinicReport` | SQLite connection string (default `/data/clinicreport.db` in the container) |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` | When behind a TLS-terminating reverse proxy |
| `Demo__Enabled=true` | Synthetic patients and one-click demo users; accounts and passwords are locked. **Never** on a server with real patients |
| `Demo__ResetHourUtc=8` | With demo mode: wipe and re-seed the demo every day at that UTC hour |

Serve it over HTTPS (for example behind Caddy or nginx). Back up the `/data` volume.

## Development

Requirements: .NET 10 SDK, Node 20+.

```bash
dotnet run --project src/ClinicReport.Api      # API on http://localhost:5290 (Development = demo data)
```

```bash
npm --prefix web install
npm --prefix web run dev                        # UI on http://localhost:5291
```

```bash
dotnet test                                     # 60+ unit and API tests
```

In Development the login page offers one-click demo users. Open two browser profiles (or one private window)
as *Typist* and *Physician* on the same report to see live co-editing.

### Layout

```
src/ClinicReport.Api/
  Domain/      template model, validator, formula language, narrative renderer (no I/O, fully unit-tested)
  Reports/     ReportService (all report changes: validation, audit, concurrency, broadcast), SignalR hub
  Endpoints/   minimal API: auth, users, patients, templates, reports
  Data/        EF Core (SQLite), migrations, seeding
web/src/       React 19 + TypeScript: worklist, report editor, template designer, print view
templates/     bundled templates, imported on first start
tests/         xUnit: domain tests and HTTP-level API tests
docs/          template format
```

## Connected to ClinicHub

ClinicReport runs on its own (its own accounts, as above) or as one of the applications of
[ClinicHub](https://github.com/ArashKeshtgar/ClinicHub), a clinic platform for Ontario. Connected, it changes in
three ways, and only when a `ClinicHub` section is configured:

- **One sign-in.** People sign in through ClinicHub (OpenID Connect, authorization code with PKCE) with the same
  password and two-factor as the clinic's other applications, and choose the clinic there. ClinicHub decides who
  may use ClinicReport: a role holding `clinicreport:reports.sign` arrives here as `reports.sign`, the permission
  ClinicReport already checks. Signing out here signs out of ClinicHub too.
- **One database per clinic.** Each clinic chosen in ClinicHub gets its own SQLite file
  (`ClinicHub:DatabasePattern`, default `clinicreport-{clinic}.db`), created with the bundled templates at its first
  sign-in. One ClinicReport can serve several clinics without their reports ever sharing a table.
- **ClinicHub has the last word.** Every few minutes (`ClinicHub:RecheckMinutes`) the session is checked with
  ClinicHub; a role taken away, "sign out everywhere" or a closed account ends it here. If ClinicHub can't be
  reached, sessions continue for `ClinicHub:OfflineGraceMinutes`.

**Orders.** ClinicHub sends exams ordered at a visit to `/fhir` as a FHIR R4 transaction Bundle (Patient, Practitioner,
ServiceRequest coded with a template key), authenticated with a token ClinicHub signs for ClinicReport. They wait on the
worklist under *Ordered from ClinicHub*; *Start report* opens a draft with the indication and referring physician filled
in. A resent order is recognised; an unknown exam is refused with an OperationOutcome.

**Results back.** Signing a report from an order sends it to ClinicHub as a FHIR DiagnosticReport (`final`, the
conclusion, and a link to the full report at `ClinicHub:PublicUrl`); an addendum sends it again as `amended`. Messages
wait in the clinic database's outbox until ClinicHub accepts them; ClinicReport authenticates with its own client id
and secret (client credentials, scope `results`). They go to ClinicHub's own `/fhir` unless `ClinicHub:ResultsUrl`
names another address, such as IntegrationHub's `http://localhost:5700/route/clinichub/fhir` (the hub forwards
ClinicReport's ClinicHub token unchanged; the token itself is still asked of ClinicHub).

**Through IntegrationHub.** `dotnet run --launch-profile via-hub` is the `connected` profile with the results
(`ClinicHub:ResultsUrl`) and the accounting (`ClinicHub:FinanceUrl=http://localhost:5700/route/finance`) sent through
IntegrationHub on :5700. To switch back, run `connected` again (or empty `ResultsUrl` and set `FinanceUrl` to
`http://localhost:5400`). Orders come through the hub when ClinicHub's own `via-hub` profile is used; nothing changes here.

**To the accounting.** With `ClinicHub:FinanceUrl` set (FinancePowerApp, the group's accounting), each signed report
of an ordered exam is sent there as a receivable from OHIP: the template's technical fee (`OHIP.Technical`) and
professional fee (`Diagnostics.Echo` / `Diagnostics.ECG`). The clinic sets the fees on Templates → *OHIP fees*; a
template without fees sends nothing. No patient or findings leave; ClinicReport uses a ClinicHub token with the scope
`finance`.

**Restricted items.** An order ClinicHub labels (the patient has a consent directive there: `R` restricted, `V` very
restricted) is closed to everyone until a user says why they need it: starting the exam or opening its report answers `403 restricted` and the screen asks
for a reason (at least 10 characters), which is logged with the user's name; the access is for that user and lasts one
hour (it is kept in memory, so a restart closes it). The label goes back on the result. The worklists list a closed order or report with the patient shown as "Restricted" and no MRN or indication, so it is not lost; the reason is in the audit log (`RestrictedOverride`).

**Cancellation.** ClinicHub withdraws an exam by sending its ServiceRequest again with status `revoked`: it leaves the
worklist if it has not been started; once its report is started the answer is `409`, and the clinic speaks to the
reading physician.

What stays here: the services a person may work on (e.g. echo only), turning someone's ClinicReport access off,
and the report-access audit log, kept in each clinic's own database. Passwords, two-factor and roles are ClinicHub's;
a person appears in ClinicReport's user list after their first sign-in.

```bash
dotnet run --project src/ClinicReport.Api --launch-profile connected   # with ClinicHub running on http://localhost:5301
```

| Setting | Meaning |
|---|---|
| `ClinicHub__Authority` | ClinicHub's address (its issuer), e.g. `https://hub.example.ca/`. Empty: standalone. |
| `ClinicHub__ClientId`, `ClinicHub__ClientSecret` | ClinicReport's registration in ClinicHub (`Sso:Clients` there). |
| `ClinicHub__DatabasePattern` | `Data Source=/data/clinicreport-{clinic}.db` in the container. |

## Hosted (several clinics, one service)

Work in progress: the multi-tenant service. With `Hosting__Mode=Hosted` one ClinicReport serves many clinics, each
at its own subdomain (`north.clinicreport.example.com`, under `Hosting__BaseDomain`) with its own accounts. ClinicHub's
sign-in is not used in this mode.

- **A catalog of tenants** (`Hosting__CatalogConnection`, `catalog.db`): each clinic's slug, name, status and
  letterhead. It holds no health information.
- **One database per tenant** (`Hosting__DatabasePattern`, `clinicreport-{clinic}.db`), created with the bundled
  templates at the tenant's first request. The host decides the tenant; a session signed in at one subdomain is
  refused at another (401). A suspended tenant can sign in, read and export, but not change anything (403). An
  unknown or cancelled one is not found (404).
- **Plans.** A sign-up starts a 14-day trial with Pro's limits, then falls to Free: 3 active users, 2 services and 50
  signed reports a month. A full limit answers 402 and stops growth only; nobody already there is locked out. Billing
  uses Stripe in test mode only (`Stripe__SecretKey`, which must be `sk_test_...`, `Stripe__ProPriceId` and
  `Stripe__WebhookSecret`, from `/run/secrets`). Without them the service runs and the billing page says it is not set
  up. This project takes no real payments.
- **Stripe's webhook** is `POST /api/billing/stripe` at the root domain, the only thing that changes a plan. Stripe
  signs each event and may repeat or reorder them, so the signature is checked, an event already processed does
  nothing, and neither does one older than the last applied. When a subscription ends the clinic is read-only for 30
  days, to export or subscribe again; after that its subdomain is not found.
- **A failed payment** makes the clinic past due, with a notice to everyone signed in there. Seven days later an hourly
  job suspends it (read-only, as above); paying ends either. A suspension by the operator stays until the operator
  lifts it, whatever Stripe reports.
- **Upgrades.** `dotnet ClinicReport.Api.dll --migrate-all` migrates every tenant's database, one after another. A
  tenant that fails is logged and the others carry on; the exit code is 1 if any failed.

Why a database per tenant rather than one shared database with a `TenantId` column: a clinic's patients are kept
apart by where they are stored, not by a filter every query has to remember. A tenant's export, backup or deletion is
one file. What it costs:

- **Migrations run N times**, once per tenant, hence `--migrate-all` before a new version takes requests.
- **One instance.** Who has a report open is kept in memory and SignalR has no backplane, so the service runs as a
  single instance.
- **SQLite.** Enough for a VPS and a few dozen clinics. Past that, the next step is PostgreSQL with a database (or
  schema) per tenant; that is not part of this work.

## How a template works

```json
{
  "id": "ms", "label": "Stenosis", "type": "choice",
  "options": [
    { "value": "none", "label": "None", "phrase": "" },
    { "value": "moderate", "label": "Moderate", "phrase": "There is moderate mitral stenosis.", "abnormal": true }
  ],
  "children": [
    { "id": "mv_mean_grad", "label": "Mean gradient", "type": "number", "unit": "mmHg",
      "showIf": { "field": "ms", "noneOf": ["none"] },
      "phrase": "The mean transmitral gradient is {value} mmHg." }
  ]
}
```

The full format (field types, conditions, formulas, normal ranges, presets) is in
[docs/templates.md](docs/templates.md).

## Roadmap

- HL7 v2 ORU export, and patient/order intake from a HIS (ADT/ORM)
- Server-side PDF and secure report delivery to referring physicians
- Measurement import from echo machines (DICOM SR)
- More templates: stress test, Holter, carotid and venous duplex, abdominal ultrasound
- Translations of the UI and bilingual templates
- PostgreSQL / SQL Server providers for larger sites

## Origin

ClinicReport grew out of an echocardiography reporting tool written in 2017 for a clinic as a Windows Forms
application. That version hard-coded the echo form; the idea it was reaching for — one reporting engine where
each service's form is data, filled live during the exam — is what this project implements.

## Disclaimer

This software is provided “as is”, without warranty of any kind (see [LICENSE](LICENSE)). It is not
cleared or certified as a medical device in any jurisdiction. Reference ranges in the bundled templates
follow the cited guidelines (Lang et al., *J Am Soc Echocardiogr* 2015; Nagueh et al., *J Am Soc
Echocardiogr* 2016) as understood by the authors and may contain errors. Clinical decisions remain the
responsibility of the signing physician. Each organization deploying ClinicReport is responsible for
validation, privacy and security obligations that apply to it (for example PHIPA in Ontario, HIPAA in the
US, GDPR in the EU), including hosting, access control, backups and audit review.

## License

[MIT](LICENSE)
