# Contributing

Thanks for helping. Two kinds of contribution are especially useful:

1. **Templates for more services** (stress test, Holter, vascular and abdominal ultrasound, pulmonary
   function, …). Add a JSON file under `templates/` following [docs/templates.md](docs/templates.md).
   Cite the guideline behind any normal range in its `source`, keep the wording neutral and in the style a
   reporting physician would sign, and run `dotnet test` — every bundled template is validated by the tests.
   A clinician reviewing the template is a great help; say in the pull request who reviewed it.
2. **Corrections** to wording, reference ranges or calculations. Please include the source.

For code changes: open an issue first for anything large, keep the existing style, and add tests
(`tests/ClinicReport.Tests`). `dotnet test` and `npm --prefix web run build` must pass.

**Never put real patient information in issues, pull requests, screenshots or test data.** Use
obviously synthetic names and MRNs (the demo data uses `DEMO-0001`, …).
