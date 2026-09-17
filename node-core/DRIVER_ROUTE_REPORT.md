# Driver Guidance

- Added `GET /api/v1/drivers/me/routes` and `GET /api/v1/drivers/me/routes/:containerId` for authenticated drivers.
- After a passed Gate-In, the API returns the container's assigned Block/Bay/Row/Tier and directions from the gate. It reads existing PostgreSQL gate, reservation, position, and route instruction data without changing the shared schema.
- If no Yard slot is assigned, the detail endpoint returns `409 YARD_LOCATION_NOT_ASSIGNED`. Directions describe Yard waypoints; turn-by-turn map geometry is unavailable in the current data.
- Verified with TypeScript build, Jest tests, and a read-only query against the current PostgreSQL schema.
