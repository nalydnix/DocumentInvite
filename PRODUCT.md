# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users
- **Requesters** (staff: Admin, LoanOfficer, Auditor roles in the current code): sign in to create requests, send invites, review and approve documents, and complete the request. Daily desk work on a laptop.
- **Recipients** (external contacts): receive an emailed link, open the portal, upload the requested documents and answer form fields. Most open it once, often on a phone, with no account.
- Terminology confirmed by the owner: "requester" and "recipient". The app is not lending-only, so "borrower" and "loan" wording is being retired (issues #83, #84).

## Product Purpose
DocumentInvite is a secure document-collection tool. A requester sends a request (a checklist of documents and form fields) to recipients, who upload through a private link. The requester reviews each item (approve or reject), messages the recipient, then completes the request and exports an organised zip. Success: every requested item is approved with a minimum of chasing.

## Positioning
Single-tenant, portfolio-grade MVP modelled on FileInvite: no recipient account, tokenised expiring links, direct-to-blob uploads, hash-chained audit trail, GDPR export and erasure.

## Operating Context
Staff work from the web app (Vercel) against an API on Azure App Service, SQL and Blob. Invites and hourly reminders go out by email (Resend). A demo mode with seeded requests exists for evaluation and must be off in production.

## Capabilities and Constraints
- Implemented: contacts, templates, requests with parties and invites, recipient portal (optional password, privacy notice), review and messaging, reminders, audit log with CSV and integrity check, zip export, GDPR tools, retention purge, outbox, users and settings.
- Not built by design: CRM or cloud-storage integrations, SSO, e-signatures, branding, virus scanning, analytics, AI.
- Stack: React, TypeScript, Vite, Tailwind, React Router, TanStack Query; tests with Vitest.

## Brand Commitments
Product name "DocumentInvite" only. No logo, palette, voice or typography is binding; the owner wants an improved color scheme and clearer UX.

## Evidence on Hand
No customers, testimonials, benchmarks or pricing exist; do not invent any. The README and `docs/architecture.md` describe the product.

## Product Principles
- Recipients should finish in one sitting without instructions: the portal explains itself and works on a phone.
- Staff see status at a glance: what is waiting on whom.
- Trust is visible: privacy and security are stated plainly, never decorative.
- Equal care for the staff app and the recipient portal.
- Wording stays general (request, recipient), never lending-specific.

## Accessibility & Inclusion
No product-specific requirement was set by the owner; default to WCAG AA contrast, keyboard operation and screen-reader labels.
