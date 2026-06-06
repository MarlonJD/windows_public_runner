# Windows Public Runner

Temporary public GitHub Actions runner for EMSI Windows WinUI 3 screenshot evidence while `winui3-mac-test-runtime` is not ready for full render evidence.

This repository must not retain EMSI source, screenshots, logs, tokens, account names, brand-private labels, or raw run artifacts. The workflow writes evidence directly to the private `MarlonJD/emsi_qa` repository and cleans the runner workspace at the end of every run.

## Required Secrets

- `EMSI_SOURCE_SSH_KEY`: private key for the read-only deploy key on `MarlonJD/emsi_monorepo`.
- `EMSI_QA_SSH_KEY`: private key for the write-enabled deploy key on `MarlonJD/emsi_qa`.
- `EMSI_WINDOWS_USERNAME`: optional disposable Windows smoke account username. If omitted, the harness uses the app's prefilled test account fields.
- `EMSI_WINDOWS_PASSWORD`: optional disposable Windows smoke account password. If omitted, the harness uses the app's prefilled test account fields.

## Run

Use **Actions -> Windows page screenshots -> Run workflow**.

Inputs:

- `source_ref`: EMSI source branch, tag, or SHA. Defaults to `main`.
- `qa_label`: destination label under `windows/page-screenshots/`. Defaults to `public-runner`.
- `language`: app language. Defaults to `tr-TR`.
- `theme`: app theme. Defaults to `system`.
- `api_base_url`: temporary HTTPS API URL, for example a `trycloudflare.com` tunnel to local `http://localhost:8080`.

The workflow captures login plus signed-in pages for Home, Channels, Events, Messages, Notifications, Settings, and Admin when visible. Screenshots are JPG evidence artifacts and are pushed only to `MarlonJD/emsi_qa`.
