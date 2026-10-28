# Repository rules

- Never commit or push to `main` directly. Every change goes through a branch and a pull request.
- Every change under `SurveyExporter/` produces a new GitHub release automatically
  (`.github/workflows/survey-exporter-release.yml`, patch bump of the latest `v*` tag on merge to `main`).
  Do not create SurveyExporter tags or releases by hand. For a minor/major bump, push the tag
  `vX.Y.0` first; the next automatic release continues from it.
- `docs/` holds the customer-facing documentation as Markdown.
