# First-run admin guide

For the person setting up a live DocumentInvite site for real users. It assumes the site is deployed as in [deployment.md](deployment.md) and e-mail delivery works ([Real e-mail delivery](deployment.md#real-e-mail-delivery-resend)). No demo data is needed at any point.

## 1. Production checklist (before first sign-in)

Check these App Service settings (Settings, Environment variables). Generate secrets with a password manager or `openssl rand -base64 48`.

| Setting | Required value |
| --- | --- |
| `Demo__Enabled` | `false`, or unset. When it is on, the sign-in page publishes demo logins. Open the sign-in page and confirm no demo logins are shown |
| `Scalar__Enabled` | `false` |
| `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` | A real admin address and a strong, unique password (12+ characters). Used once at first start |
| `Portal__Session__SigningKey` | A new random base64 key of at least 32 bytes, for example `openssl rand -base64 48`. It signs borrower portal sessions. Changing it signs every borrower out, so rotate it before real invites exist or at a quiet time |
| `Jobs__ApiKey` | A new random key, equal to the `REMINDERS_API_KEY` GitHub secret |
| `Portal__BaseUrl` | The live web origin, no trailing path |
| `Email__Provider`, `Email__From`, `Resend__ApiKey` | `Resend`, an address on a verified domain, and the API key |

Rotating the secrets:

1. **Bootstrap admin password.** The app has no password-change screen, so the password you set in `Bootstrap__AdminPassword` before the first start is the Admin's password. Make it strong and unique, and never reuse a demo or development password. After the first start, delete `Bootstrap__AdminEmail` and `Bootstrap__AdminPassword` from the App Service so the password is not kept in configuration. If the Admin was already created with a password that must be replaced, create a second Admin (step 3), then, as the SQL admin, delete the old account (`DELETE FROM dbo.AspNetUserRoles WHERE UserId = '<id>'; DELETE FROM dbo.AspNetUsers WHERE Id = '<id>';`) and have the new Admin confirm they can sign in first. Audit events keep only ids, so the audit chain stays valid.
2. **Signing key.** Set a new `Portal__Session__SigningKey`, save, and let the app restart. Without it sessions are lost on every restart.
3. After any settings change, wait for the restart, then open `<api-url>/health` to confirm the API is up.

## 2. Sign in

1. Open the web app address (for example `https://documentinvite.vercel.app`) and go to the sign-in page.
2. Sign in with the bootstrap admin e-mail and password.

## 3. Create staff users and roles

Users, in the left menu (Admin only).

| Role | Can do |
| --- | --- |
| Admin | Everything, including users, settings, retention and the audit log |
| Requester | Create and manage requests, templates, contacts, invites and messages |
| Auditor | Read the audit log. Access has an expiry date that you set |

1. Open **Users**.
2. In **Create user**, enter the person's e-mail and a temporary password (12+ characters), choose a role and click **Create user**. For an Auditor, set an access expiry date.
3. Give each person their password through a separate, secure channel.
4. Create a Requester for day-to-day use, and a second Admin so the site is not locked if one person is away.

## 4. Review templates

Templates, in the left menu. Two lending templates are provided (SBA and commercial real estate). Open each one and check the item names and instructions suit your business. Edit them, or create a new template with **New template** (name, then the items to collect, in order). Archive templates you will not use. Invites copy the template items, so later template edits do not change invites already sent.

## 5. Add a contact

Contacts, in the left menu. Click to add a contact (first name, last name, e-mail). Use the recipient's real address; the invite link is sent there. Contacts are the people who complete requests.

## 6. Create the first request and send an invite

1. Open **Requests** and click to create a new request. Enter the reference, request name, amount and request type, then save.
2. On the request page, open the invites section: **Add party**, choose the contact, choose a template, and tick the items they should receive. Edit item names or instructions if needed and set the dates.
3. Click **Send invites**. The recipient gets an e-mail with a secure link.
4. Open **Outbox** and check the e-mail shows as sent. If it failed, see Troubleshooting in [deployment.md](deployment.md#real-e-mail-delivery-resend).
5. Ask the recipient to open the link, accept the privacy notice and upload. You will see their uploads on the request page, where you can approve or reject each item. When every item is approved, click **Complete** on the request.

To test before inviting a customer, send the first invite to your own address and complete the upload yourself.

## 7. Retention and privacy

- **Retention.** Settings, Retention after completion (days). The default is 365: a completed request's files and data become eligible for deletion 365 days after it was completed. Choose the period your policy or regulator requires. Deletion is never automatic: an Admin opens Settings, uses the retention preview to see what would be removed, then runs the purge. Do this on a schedule that you decide, for example monthly.
- **Requests and erasure.** To answer a data export or erasure request from a borrower, open the contact (Contacts, then the contact) and use its data export and erase panel (erasure is Admin only). Both are written to the audit log.
- **Privacy notice.** Borrowers see a privacy notice on their first visit and must accept it before uploading or messaging. The text is in `src/DocumentInvite.Web/src/portal/PortalPage.tsx` (`PrivacyNotice`). It states what is collected, who can see it, that it is deleted after a limited period, and that they can ask for export or erasure. Have your legal adviser review it and adjust it to your retention period before going live.
- **Help.** The portal ends with a short "Need help?" section. Edit it in the same file if your process differs.
- **Audit.** Review the Audit log regularly, and run the integrity check after deployments that change data.

## 8. Go-live check

- [ ] No demo logins on the sign-in page
- [ ] Bootstrap settings removed, signing key and jobs key set
- [ ] A test invite arrived in a real inbox and the link opened the portal
- [ ] A test upload appeared on the request page
- [ ] Retention period confirmed
- [ ] A second Admin exists
