# Driver accounts and fleet management

Driver account provisioning, linking, email activation and resending are owned by C#.
The frontend uses the C# /api/v1 proxy. Node still serves the existing general login,
heartbeat and driver journey workflows, but no longer exposes driver-account writes.

## Stored account state

GET /api/v1/Driver reads users.invitation_pending, users.email_verified_at and
users.is_active together with the company-scoped driver list. There is no extra
Node request to recheck email state. Lists do not update license statuses or scan
and ban drivers belonging to other companies. License expiry is checked when
selecting/assigning resources; the UI also shows expiry warnings.

| C# endpoint | Access | Result |
| --- | --- | --- |
| POST /api/v1/driver-accounts | Transport Company | Create an inactive account and profile, or link an existing unlinked profile, then send confirmation email |
| POST /api/v1/driver-accounts/{driverId}/resend | Owning company / Administrator | Replace an unverified account's invitation with a new 24-hour link |
| POST /api/v1/driver-accounts/activate | Invitation holder | Verify email, set password and activate the account |

Company identity comes from active DB membership. Account status is distinct from
session authentication: a missing/ambiguous company returns 403, while an invalid
login session returns 401. The frontend ignores an old request's 401 after a new
login, and keeps sessions on timeouts, network failures and 403 responses.

The existing account_requests and account_invitations tables remain the audit and
invitation storage. No new migration or backfill is required beyond the develop
schema through 20261114_048_require_driver_verification.sql. Existing emailed links
remain valid because token hashing, expiry, URL fragments and password format are
unchanged. Creation is serialized, username/email collisions are rejected, and
email delivery failure rolls back account/profile/invitation changes. A delivery
succeeded immediately before a failed commit can still produce an unusable email;
resending recovers that rare case.

Activation checks the stored invitation against the current account email, expiry,
used flag and blocked/pending state. Passwords use bcrypt cost 12, compatible with
the existing login service. The library is [BCrypt.Net-Next](https://github.com/BcryptNet/bcrypt.net).
No email or login checks are performed as side effects of loading a list.

## C# configuration

Set AccountInvitations__FrontendOrigin to the frontend origin, and configure the
existing EmailSettings__SmtpHost, SmtpPort, SmtpUser, SmtpPass and EnableSsl settings.
Development uses ignored appsettings.Local.json. Run ./csharp-core/run.ps1 or dotnet run --project csharp-core/src/NexusPort.Api; the local launch profile sets Development so AWS/mail settings are loaded. Production uses environment variables/role credentials and does not load local secrets. SMTP delivery is called directly
inside C#: ACCOUNT_EMAIL_SERVICE_URL, ACCOUNT_EMAIL_SERVICE_KEY and the old
/internal/account-email relay are no longer needed. Never commit mail credentials.

## Fleet schedule and assignments

GET /api/v1/Booking/fleet-schedule?from=<UTC>&to=<UTC> accepts a positive window up
to 24 hours. The UI provides a Vietnam-time date with full-day, morning and afternoon
windows. It includes company bookings, live transport trips and service draft
reservations. Unfinished trips and an unclosed physical gate entry stay busy after
the planned end. Free resources use the same conflict policy as booking validation;
maintenance, disabled, unverified linked accounts and invalid licenses are excluded.
The response omits document images and booking snapshots.

GET /api/v1/Booking/{id}/fleet-options returns lightweight available choices for
editing a company's booking. POST /api/v1/Booking/{id}/assign rechecks conflicts
under resource locks and preserves Approved status. Checked-in/operational trips
are not reassigned here. Default vehicle-driver assignments are separate from
booking assignments: PATCH /api/v1/Vehicle/{id}/assign-driver validates company,
verified account, license and existing default assignments in one transaction;
it does not change the driver's transport workflow status.

## Verification

- Build frontend with npm run build; run node --test tests/fleetApiClient.test.mjs.
- Build C# tests/FleetManagementChecks and run its DLL. --postgres additionally
  requires an isolated PostgreSQL instance at 127.0.0.1:55440, database/user
  fleet_check, with database comment isolated-fleet-checks-20261114.
- The PostgreSQL runner refuses an unmarked database, uses a unique schema with
  synthetic accounts/resources, captures emails in memory, and removes that schema
  afterward. It never loads application credentials or sends real messages.
- Rebuild/restart the C# API and frontend together when testing the migrated routes.

Fleet lists request includeDocuments=false and includePhotos=false: the data-list path
never signs image URLs. HasPhoto indicates whether an optional avatar exists. After
rendering the data, the frontend requests company-scoped GET /api/v1/Driver/photos
or /api/v1/Vehicle/photos with repeated ids parameters (maximum 100 per batch).
Avatar errors leave the list and counters usable. Document images load when a
detail is opened. AWS S3 remains the storage backend. The S3 client is shared, and
a credential lookup failure is retried after one minute instead of once per image.
The two data requests each use four SQL commands, including membership and
name/account enrichment; optional photos are separate requests.

Only the login authority can end the frontend session. Business-service 401s are
checked against /api/auth/me before clearing credentials. The authority must return
an explicit TOKEN_EXPIRED, TOKEN_INVALID, SESSION_KICKED, SESSION_LOGGED_OUT or
ACCOUNT_DISABLED code. Unknown proxy responses and network failures preserve the
session; expired, replaced and disabled sessions remain rejected by the backend.
