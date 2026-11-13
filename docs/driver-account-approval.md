# Driver account approval

Implemented on `fix/driver-account-approval`, based on `origin/develop`.

`users` owns username, email, password hash, role, activation and session state.
`drivers` owns identity documents, license, contact and operational status, with
an explicit unique `user_id` link. `carrier_users` owns company membership;
`shipping_line_accounts` already separates shipping-line membership. Retain the
single `varchar` role with controlled values for now. Add a role/permission
catalog only when dynamic permissions or multiple simultaneous roles are needed.

Only Driver requests are implemented. The administrator's user management page
contains the request queue. Manual Driver creation and Driver role assignment
through the legacy Express API return 409/410. Existing management for other roles
is retained; no new request flows for those roles are introduced.

## Configuration

Node Core `.env`:

```
ACCOUNT_ACTIVATION_ORIGIN=http://localhost:5173
ACCOUNT_EMAIL_SERVICE_URL=http://localhost:5000/internal/account-email
ACCOUNT_EMAIL_SERVICE_KEY=<random shared service secret>
```

C# environment/configuration:

```
AccountInvitations__FrontendOrigin=http://localhost:5173
AccountInvitations__ServiceKey=<same shared service secret>
EmailSettings__SmtpHost=smtp.gmail.com
EmailSettings__SmtpPort=587
EmailSettings__SmtpUser=<configured mailbox>
EmailSettings__SmtpPass=<configured app password>
EmailSettings__EnableSsl=true
```

Use the real HTTPS frontend origin in deployment. Keep the service endpoint on
the internal network, use HTTPS when crossing hosts, and keep the secret out of
frontend build variables. Node refuses approval if configuration/delivery fails;
the C# endpoint returns 503 if SMTP credentials are missing. Emails use the
existing C# `IEmailService`. Driver requests, account creation, linking, token
issuance, validation and password creation are TypeScript.

Development can also load the ignored `appsettings.Local.json`; environment
variables override that file. This worktree's local mail settings and shared key
are configured without adding them to tracked files.

Apply `20261114_044_driver_account_requests.sql` and
`20261114_045_driver_user_integrity.sql` before starting these services. Migration
045 restores the driver-to-user FK removed by historical development sync.
Start C# :5000, Node :4000 and the frontend :5173 using the existing proxy config.

## API

All paths below use the Node `/api/v1` prefix (frontend `/node-api`).

| Method/path | Access | Result |
| --- | --- | --- |
| POST `/account-requests` | Transport Company | Submit profile, username and email; company resolved from DB membership |
| GET `/account-requests` | Administrator / Transport Company | Admin queue / own company requests |
| GET `/account-requests/driver-links` | Administrator / Transport Company | Explicit account/profile links in allowed scope |
| POST `/account-requests/:id/approve` | Administrator | Create inactive account, link driver and company, email invitation |
| POST `/account-requests/:id/reject` | Administrator | Reject pending request |
| POST `/account-requests/:id/resend` | Administrator | Replace unused invitation for an approved inactive account |
| POST `/auth/activate-account` | Invitation holder | Supply token and new password, consume token and activate account |

Tokens contain 256 random bits; DB stores SHA-256 hashes only. Invitations expire
exactly 24 hours after issue, work once, and are revoked by resend. The frontend
reads the token from the URL fragment and removes it after mounting. A DB guard
blocks manual activation while invitation acceptance is pending. Passwords use
bcrypt with cost 12, accept 10–72 characters, and are never displayed to company
staff or administrators. Existing passwords are not reset by data cleanup.
New driver profiles remain `inactive` until activation; successful activation
makes them `active`. Existing profiles retain their operational history/status.
Direct POST creation on the legacy C# and Node driver endpoints returns 410.

An email is sent inside the approval transaction. If mail delivery fails the
transaction rolls back; if delivery succeeds but the DB commit subsequently
fails, that email link cannot activate an account. Retry approval/resend in that
rare case. A durable mail outbox can replace this coupling in a later change.

Berth Staff pages, menu, routes, role choices, permissions and demo seed account
are removed. Historical migrations remain unchanged. Operational berth master
data and booking history remain available to existing authorized roles.

## Data repair and checks

`src/scripts/repair-driver-accounts.ts` is explicit one-off maintenance, never
called at startup. Preview without flags; applying requires `--apply` and a new
`DRIVER_REPAIR_BACKUP` path. It preserves all booked profiles, backs up records
before edits, restores missing user IDs with invitation activation pending, adds
company memberships, removes unbooked test profiles/accounts, and verifies every
booking's ID/driver/company is unchanged before commit. The historical backup
identifies `e738b67f-dba6-4f5e-895f-eb2e81afe3bc` as `tiendriver`. The user explicitly
authorized assigning `dauboquay9` to any profile with an empty user link.

Migration `20261114_046_unblock_legacy_driver_accounts.sql` exempts the nine audited
legacy driver accounts (including recovered accounts) from invitation activation,
as explicitly requested. They are active with `invitation_pending=false`; existing
password hashes are preserved. Recovered accounts whose original passwords were
lost can use the existing password reset flow. New accounts created through
approval remain blocked until invitation acceptance. The migration uses exact
legacy IDs and never unblocks newly approved accounts.
`dauboquay` remains a Transport Company account; existing passwords are preserved.

Real HTTP/SQL integration (local email receiver, no messages to real recipients):

```powershell
$env:DATABASE_URL='postgresql://postgres@127.0.0.1:55438/nxp038_test'
$env:DB_SSL='false'
npm run db:migrate
npx tsx src/scripts/check-driver-account-requests.ts
```

The check rejects all other DB targets and removes its fixtures. It covers tenant
spoofing, permissions, concurrent approvals, mail failure rollback, account and
company links, expiry, resend revocation, single use, manual activation guards
and login with the newly set password.
