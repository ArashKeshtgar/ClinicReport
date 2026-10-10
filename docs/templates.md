# Template format

A template is one JSON document. The designer in the app edits it visually; the **JSON** tab, the
**Import JSON** button and the files in [`templates/`](../templates) use this format directly. Comments
(`// …`) and trailing commas are allowed in files. Unknown properties are rejected, so a misspelt
`"phrse"` is reported instead of silently ignored.

```json
{
  "key": "echo-tte",
  "name": "Transthoracic Echocardiogram (TTE)",
  "service": "Echocardiography",
  "description": "optional",
  "sections": [ … ]
}
```

| Property | Rules |
| --- | --- |
| `key` | Lower-case letters, digits, dashes. Stable forever: reports and exports refer to it. |
| `name` | Shown on the worklist and printed as the report title. |
| `service` | Groups templates (e.g. "Echocardiography", "Electrocardiography"). |

## Sections

```json
{
  "id": "lv",
  "title": "Left ventricle",
  "narrative": true,
  "normalPreset": { "lv_size": "normal", "lv_function": "normal", "rwma": "none" },
  "fields": [ … ]
}
```

- `narrative: false` keeps the section's sentences out of the findings (useful for a measurements-only
  section; its numbers still go to the measurement table).
- `normalPreset` powers the **All normal** button: field id → value. Values must fit the field (an option
  value, a list of option values, a number or text).

## Fields

Every field has an `id` (starts with a letter; `a-z`, `0-9`, `_`; unique in the whole template), a `label`
and a `type`.

| `type` | Typist sees | Stored value |
| --- | --- | --- |
| `choice` | buttons, one can be picked | option value (string) |
| `multi` | toggle buttons, any number | list of option values |
| `number` | number box with unit and range | number |
| `computed` | calculated value, read-only | not stored; calculated on render |
| `text` | one line | string |
| `textarea` | paragraph | string |

Other properties:

| Property | Applies to | Meaning |
| --- | --- | --- |
| `required` | all but computed | Must be answered (when visible) before a physician can sign. |
| `hint` | all | Help text shown as a “?” tooltip. |
| `unit`, `decimals` | number, computed | Display. Computed fields default to 1 decimal. |
| `normal` | number, computed | Normal range, see below. |
| `measurement` | number, computed | `false` keeps it out of the measurement table. |
| `formula` | computed | See [Formulas](#formulas). |
| `options` | choice, multi | `[{ "value", "label", "phrase"?, "abnormal"? }]` |
| `phrase` | all | Sentence template, see [Sentences](#sentences). |
| `showIf` | all | Show only for certain answers of another field, see [Conditions](#conditions). |
| `children` | all | Follow-up fields, shown indented under this one (and hidden whenever it is hidden). |

### Sentences

The findings paragraph of a section is the sentences of its visible, answered fields, in order.

- **choice** – the chosen option's `phrase`, else the field's `phrase`, else `"{label}: {value}."`.
- **multi** – the field's `phrase` (default `"{label}: {values}."`) once, with `{values}` the picked options
  joined as “a, b and c”. An option's `phrase` replaces its label inside that list.
- **number / computed** – only when the field has a `phrase`, e.g. `"The ejection fraction is estimated at {value}%."`
- **text** – `phrase` or `"{label}: {value}."`; **textarea** – `phrase` or the text itself.

Placeholders: `{label}`, `{value}`, `{unit}`, `{values}`. An empty phrase (`""`) means *say nothing*, which is
how “None” or “Good image quality” stay out of the report. The first letter is capitalized and a full stop
added if missing.

`"abnormal": true` on an option marks the sentence as a finding: it is offered in **Insert abnormal
findings** for the conclusion. Numbers with a phrase count as findings when they fall outside their range.

### Conditions

```json
"showIf": { "field": "mr", "anyOf": ["mild", "moderate", "severe"] }
"showIf": { "field": "ms", "noneOf": ["none"] }
"showIf": { "field": "lvef", "lessThan": 50 }
```

- `anyOf` – the other field's answer is one of these (for `multi`: any of these is picked).
- `noneOf` – the other field is answered with something other than these.
- `greaterThan` / `lessThan` – numeric field (typed or computed) compared with a number.

A field whose condition refers to a hidden field is hidden too, so chains collapse correctly. Hidden fields
keep any old answer but never reach the report.

### Normal ranges

```json
"normal": { "min": 16, "max": 34, "source": "ASE/EACVI 2015" }
"normal": { "male": { "min": 42, "max": 58 }, "female": { "min": 38, "max": 52 } }
"normal": { "min": 17 }
```

The patient's sex picks `male` / `female` when given; otherwise the general `min`/`max` is used. If only
sex-specific limits exist and the sex is unknown or X, no range is shown and nothing is flagged. Values
below `min` are flagged **L**, above `max` **H**.

### Formulas

Computed fields use a small arithmetic language — no code execution:

- numbers, other `number`/`computed` field ids, and the built-ins `height` (cm), `weight` (kg), `age` (years)
  from the report header and patient;
- `+ - * / ^` (power is right-associative) and parentheses;
- `sqrt(x)`, `abs(x)`, `min(a, b, …)`, `max(a, b, …)`, `avg(a, b, …)`, `round(x, digits)`.

If any input is blank the result is blank, except `avg`, which ignores blanks. Division by zero gives blank.
Cycles between formulas are rejected.

Examples from the bundled templates:

```text
bsa     sqrt(height * weight / 3600)                                   Mosteller
lv_mass 0.8 * (1.04 * (((lvidd + ivsd + lvpwd) / 10) ^ 3 - (lvidd / 10) ^ 3)) + 0.6
e_e     e_wave / avg(e_sept, e_lat)
rvsp    4 * tr_vmax ^ 2 + rap
qtc     qt / sqrt(60 / heart_rate)                                     Bazett
```

## Versions

Saving in the designer writes a **draft**; it may have problems, which are listed and block publishing.
**Publish** validates the draft and makes it the version new reports use. Published versions never change;
a report keeps rendering with the version it was started on. **Export JSON** downloads the latest published
version for sharing with another clinic.
