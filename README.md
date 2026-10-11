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

- HL7 v2 ORU / FHIR `DiagnosticReport` export, and patient/order intake from a HIS (ADT/ORM)
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
