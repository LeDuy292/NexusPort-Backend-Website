# Company-managed driver accounts

Branch: `fix/driver-account-approval`, based on `origin/develop`.
The previous admin-approval implementation was committed and pushed first:
backend `e864e5c`, frontend `582faaa`. The current implementation removes that
approval workflow. No administrator action is required to create driver accounts.

## User flow

The Transport Company's existing **Thêm Tài Xế Mới** dialog keeps its identity
document uploads/OCR and license fields, and includes **Tên đăng nhập** and
**Email nhận xác nhận**. Clicking **Tạo tài khoản** immediately creates the driver,
an inactive login and company membership, then sends a verification email through
the existing C# mail service. Company staff never choose or see the password.

The driver opens the email link and creates a password within 24 hours. Successful
activation records email verification, consumes the link, enables login, activates
new driver profiles and revokes prior login sessions. Expired/used links cannot be
accepted. The company can send a fresh link from **Gửi lại link** on that driver's
card or detail; the previous link is revoked. The page offers **Làm mới trạng thái**.

Each linked driver's card/detail shows **Đã xác minh** or **Chưa xác minh** based
on `users.email_verified_at`, immediately after the name. Unverified drivers have
the resend action and cannot log in or restore old sessions. A resend never
changes their password. Exactly three explicitly approved legacy test accounts
are administratively marked verified: Gia Huy\8k, Nguyen Dinh Tien (`tiendriver`)
and Phan Nguyen Gia Huy with license `660225020133` in the displayed company.
Every other driver must confirm email through an invitation. Suspended accounts outside invitation onboarding
cannot be reactivated through resend. Profiles without a login show **Chưa có tài
khoản** and have no resend action. The account fields on editing an unlinked
profile can also provision its login without creating a duplicate driver.

The admin request queue and company request list are removed. Other existing
admin user management is retained. The legacy direct Driver creation endpoints
remain retired, so password provisioning cannot bypass email activation.

## Data and API

`users` owns authentication/contact/session state and the single controlled role
string. `drivers` owns identity documents, license and operational information,
with an explicit unique `user_id`. `carrier_users` owns company membership;
`shipping_line_accounts` separately owns shipping-line membership. A dynamic
role/permission catalog is not required for the current single-role model.

The existing `account_requests` table is retained for historical compatibility and
as the parent for invitation/audit records. Self-service provisioning inserts its
record with status `approved`, `requested_by` set to the company actor, and no
reviewer. It never queues a request. Prior migrations remain unchanged.

Node `/api/v1` endpoints (frontend proxy `/node-api`):

| Endpoint | Access | Result |
| --- | --- | --- |
| POST `/driver-accounts` | Transport Company | Create inactive Driver login/profile and send confirmation email |
| GET `/driver-accounts/driver-links` | Company / Administrator | Allowed profiles' login links, email proof and invitation expiry |
| POST `/driver-accounts/:driverId/resend` | Owning company / Administrator | Send a new 24-hour email link for an unverified account |
| POST `/auth/activate-account` | Invitation holder | Confirm email, set password and enable login |

Company identity comes from active DB membership, never a submitted carrier ID.
Creating/linking and resending are scoped to that company. Duplicate account
creation is serialized and rejected. Email failure rolls back account, driver and
invitation changes. A successful email followed by a failed DB commit can leave an
unusable email; retry creation/resend in that rare case. A mail outbox can replace
this transaction/delivery coupling later.

Invitation tokens contain 256 random bits; DB stores SHA-256 hashes only. Links
are single-use and expire exactly 24 hours after issue. The frontend reads the
token from the URL fragment and removes the fragment after mounting. Activation
also checks the account's current email against the invitation recipient.
Passwords use bcrypt cost 12 (10 characters minimum, 72 UTF-8 bytes maximum).
A DB guard prevents manually activating accounts still pending invitation.

## Migration and configuration

Apply every missing migration in filename order before starting Node Core.
`20261114_047_driver_email_verification.sql` adds nullable `email_verified_at`;
it does not change legacy account activation, passwords, bookings or company
links. Earlier 044–046 provide invitations, restored driver/user FK integrity and
the explicit nine-account legacy unblock. The previous data repair preserved all
33 bookings and linked all nine retained driver profiles to users and companies.
Subsequent explicitly requested cleanup backed up and removed five invalid/test
drivers and their ten bookings; 23 other bookings and all physical containers
were preserved. Migration `20261114_048_require_driver_verification.sql` supersedes
the earlier blanket legacy unblock: only the three approved legacy IDs are marked
verified with the requested test password. Other unverified drivers are inactive
and awaiting email activation. DB guards, Express/TS login and session checks,
Socket.IO authentication, and C# Driver token validation all enforce this rule.

Node `.env`:

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

Development may also use ignored `appsettings.Local.json`; environment variables
override it. Local mail settings and the shared service key are configured in this
worktree without committing them. Use the real HTTPS frontend origin and internal
service endpoint in deployment. C# sends mail using the existing `IEmailService`;
it returns 503 if SMTP credentials are missing. All account workflow logic is TS.

The main backend/frontend repository directories are now on
`fix/driver-account-approval`; their development services run from those main
directories on C# :5000, Express :3001, Node :4000 and frontend :5173. The old
authen worktrees are detached snapshots. Apply migrations before startup;
Sequelize development sync is opt-in through `DB_SYNC=true` so restarting Express
cannot silently change shared migration-owned tables or driver login links.

## Verification

```
npm run build
```

Real HTTP/SQL integration uses only the isolated test database, a local email
receiver and disposable fixtures. No email is sent to real recipients:

```powershell
$env:DATABASE_URL='postgresql://postgres@127.0.0.1:55438/nxp038_test'
$env:DB_SSL='false'
npm run db:migrate
npx tsx src/scripts/check-driver-account-provisioning.ts
```

The check covers company creation with no admin, duplicate races, mail rollback,
tenant isolation, actual verification metadata, company resend, expiry/revocation,
single use, password login, manual-activation guards and legacy account access.
