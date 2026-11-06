# Transport Company export preparation

Migration: `20261106_033_export_preparation.sql`. All routes below use authenticated
C# `/api/v1/Booking/export`. Company membership is resolved on the server.
This implementation changes customer Booking only; Shipping Line and port UIs
must call these real endpoints to participate. Their `/api/shipping` in-memory
adapter is not updated by this change.

| Actor / action | Route | Result |
| --- | --- | --- |
| Customer: catalog | `GET options` | Real company declarations, active lines and open voyages; capabilities identify staged preparation support |
| Customer: private drafts | `GET drafts`, `GET drafts/{id}`, `PUT drafts/{id}` | Save incomplete content; expectedVersion and requestId prevent overwrites and duplicate saves |
| Customer: request / revise demand | `POST requests` | Capacity demand or selected source containers; no vehicle or VGM prerequisite; Pending |
| Assigned line: commercial decision | `POST {id}/approve`, `POST {id}/reject` | Approved / WaitingContainers, or Rejected; no truck appointments created for staged requests |
| Customer: readiness | `POST preparation/readiness` | Missing cargo-specific information; a successful read is not approval |
| Customer: completed container version | `POST {id}/preparation/confirm` | WaitingContainerReview; candidate list remains separate from the official port list |
| Assigned line: review list | `POST {id}/container-review` | `{expectedVersion, approve, reason?}`; approval uses validated sources and forwards the list; refusal requires a reason |
| Customer: file upload | `POST documents` multipart `file` | S3 upload and company-scoped documentId, hash, original name and size; no local fallback |
| Customer: SI hints | `GET {id}/si-suggestions` | Own confirmed SI counterparties and exact aggregate cargo weight when complete |

`ExportPreparationContent` has `schemaVersion=1`, optional `bookingId`,
`shippingLineId`, `voyageId`, `mode=containers|capacity`, `description`,
`equipmentRequests=[{typeCode,quantity}]`, `containers`, `sharedDocuments`, and
`sharedDocumentsConfirmed`. Each container uses its exact `containerId` and
`declarationId`, plus seal, verified `vgmKg`, applicable temperature/UN/class and
its own document references. Source metadata is reloaded from the private declaration.

`PUT drafts/{id}` accepts `{requestId,expectedVersion,content}`. New drafts use
version 0; successful saves increment version. A stale save returns HTTP 409 with
code `EXPORT_DRAFT_CONFLICT`. A draft does not reserve physical containers or fleet.
The UI keeps unsaved edits on conflict until the user explicitly loads the latest
draft. It never automatically overwrites another tab's version.

`POST requests` accepts `{requestId,bookingId?,expectedVersion?,draftId?,
expectedDraftVersion?,content}`. `bookingId` revises the same Pending/Rejected
staged request. Request IDs are idempotent for the same normalized content.
Selected containers are reserved only when actually submitted. Cancel/reject
release these reservations; closure/departure release the applicable sources.
Capacity requests reserve no fabricated container identity.

Confirmation accepts the same envelope. It requires actual VGM, seal, cargo-specific
instructions, the exact approved equipment quantities, current cut-offs and no
conflicting source reservation. Shared evidence must have a user-confirmed scope
covering the whole selected list. A file reference uses `documentId`; caller URLs,
names and sizes are replaced with authorized stored metadata. Reading an authorized
draft or dossier renews the S3 URL from its durable object key. The metadata remains
canonical; temporary URL signatures never define request identity.
Legacy URLs without company-verified file metadata are preserved for compatibility
and are never used as authority to issue a new S3 signature.

Line review is a distinct versioned decision. WaitingContainerReview and
ContainerRevisionRequested are `portStatus` values on a commercially Approved
request; neither permits a truck booking. On acceptance, status becomes
ContainersDeclared and portStatus Accepted. Accepted means the same-application
port adapter received the official list; individual truck appointments are still
Pending until processed by the port. Old `POST containers` cannot bypass this
review for staged requests.

After receipt, existing single/batch gate planners validate current free trucks,
drivers and real trailer history. Stored documents and cold/DG instructions carry
over. There is no vehicle payload condition. Existing import and combined
outbound/inbound journeys keep their earlier workflow and APIs.

SI remains independent of Gate In. Party templates come only from this company's
confirmed SI. Packages are never copied from an old shipment. Cargo weight is
calculated only when every official container has a known positive cargo weight;
VGM is never substituted for cargo weight.
Excluded/rolled containers are excluded from the sum; an incompletely declared
shipment never receives a purported full-shipment weight suggestion.

Document OCR, an authoritative trailer registry, published terminal capacities,
expiring fleet holds and partial-roll/split-B/L handling require the relevant
owning modules. No camera OCR endpoint, invented capacity or fabricated vehicle
assignment is used as a substitute.

Validation:

```powershell
node database/scripts/check-export-preparation.cjs <marked-local-runtime.json> <NexusPort.Api.dll>
```

The checker refuses any database other than marked `export_check` at
`127.0.0.1:55441`; it creates fixtures only there. Apply the migration twice on that
disposable cluster first to verify idempotency. Frontend checks run with
`node tests/export-preparation.test.mjs`, and the production frontend/C# builds.
