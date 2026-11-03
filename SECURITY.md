# Security policy

Buoy Money handles people's money data, so I take security reports seriously and answer them
first.

## Reporting a problem

Please report security problems privately, not in a public issue:

- Use this repository's **Security** tab, then **Report a vulnerability** (GitHub private
  vulnerability reporting), or
- email **support@buoy-ai.com (subject "Security")**.

Include what you found, how to reproduce it, and which version you tested (`/api/system/version`
on your server shows it). I will reply within 3 days, keep you updated while I fix it, and credit
you in the release notes if you want.

## What is in scope

- This repository: the self-hosted server, the web and mobile app, and the install files.
- The hosted service at buoy-ai.com and BuoyAI Cloud (cloud.buoymoney.com). Please do not run
  automated scans against them, access other people's data, or degrade the service. Test against
  your own self-hosted server where you can.

## Supported versions

Security fixes go into the latest release. Update with `docker compose pull && docker compose up -d`.
