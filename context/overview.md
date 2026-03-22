# Iran Conflict Map — Overview

## What It Is

A real-time military conflict tracker covering US-Iran-Israel operations, deployed at
`conflictmap.chrisdargis.com`. Ingests daily reports from CTP-ISW (Critical Threats Project),
extracts structured strike events using Claude AI, and displays them on an interactive Leaflet
map with actor filtering and date navigation.

## Current Status

**Production and running.** The full ingestion pipeline (email → extract → sync → processor →
DynamoDB → API → map) is operational. Seed data loaded from 2026-02-28 onward. CI/CD via
CodePipeline auto-deploys on push to main.

The admin review queue (for ambiguous AI extractions) has backend endpoints and a minimal
admin.html but the frontend UI is incomplete.

## Stack

| Layer | Technology |
|---|---|
| Frontend | Vanilla JS + Leaflet.js, static HTML deployed to S3/CloudFront |
| API | .NET 8 (C#) minimal API, AWS Lambda + API Gateway HTTP API |
| Database | DynamoDB (PAY_PER_REQUEST) |
| AI | Claude Sonnet 4.6 via Anthropic API |
| Email ingestion | AWS SES → S3 |
| Pipeline | SQS FIFO queues + Lambda chain |
| Infrastructure | AWS CDK 2.x (C#) |
| CI/CD | AWS CodePipeline (GitHub → main branch) |
| Domain | Route 53 + CloudFront + ACM |

## Source Data

CTP-ISW publishes a daily Iran Update report (~18:00 ET). The pipeline receives it via email
subscription (`criticalthreats@aei.org`), resolves the canonical report URL, fetches the page,
and sends it to Claude for structured extraction.
