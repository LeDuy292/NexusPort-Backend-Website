# Session checks and SQL logs

The website SessionListener retains develop's session-kick and server-disconnect
listeners. HTTP `/api/auth/me` is a fallback every 15 seconds on visible tabs,
plus focus/visibility checks. Concurrent checks are deduplicated and focus events
within two seconds share the previous check. Cleanup aborts pending HTTP work;
a stale 401 from an older account cannot clear a newer login. Network failures
do not log the user out. Multiple visible tabs still each have their own check.

Express authenticate loads the current active account once for each request.
`/auth/me` reuses that result rather than querying the same account again.
There is no account cache between requests, so account deactivation is still
checked on the next request. Responses continue to use `toSafeObject()`.

SQL tracing is off by default. Set `DB_LOG_SQL=true` in backend .env and restart
Express to temporarily print queries for debugging. Database connection errors,
application errors and HTTP request logs are unchanged. A successful HTTP
`GET /api/auth/me 200` about every 15 seconds on each visible tab is expected.

Validation: website heartbeat tests cover hidden tabs, overlap/debounce, 401,
network failure, changed account and cleanup. Express auth-me-request tests mock
the DB and verify one read/request, inactive/anonymous denial and safe output.
