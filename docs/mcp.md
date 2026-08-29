# Odyssey MCP executor

Odyssey is the file-system executor. The connected AI is an orchestrator: it can search, reason, propose an operation, poll its status, and request execution after the user approves the immutable plan in Odyssey. No MCP tool can approve an operation.

## Local stdio mode

Packaged releases contain `Odyssey.Mcp` on Linux and `Odyssey.Mcp.exe` on Windows. A development checkout can use `dotnet run --project src/Odyssey.Mcp`.

Example Claude Desktop or another stdio MCP client configuration:

```json
{
  "mcpServers": {
    "odyssey": {
      "command": "/absolute/path/to/Odyssey.Mcp",
      "args": []
    }
  }
}
```

On Windows use the full path to `Odyssey.Mcp.exe`. The MCP process writes protocol messages only to standard output; diagnostics go to standard error.

## Authenticated Streamable HTTP

HTTP mode is opt-in and requires a bearer token with at least 32 UTF-8 bytes:

```bash
ODYSSEY_MCP_TOKEN="replace-with-a-long-random-secret" \
ODYSSEY_MCP_URLS="http://127.0.0.1:47831" \
./Odyssey.Mcp --http
```

The endpoint is `http://127.0.0.1:47831/mcp`. Send the token as `Authorization: Bearer …`. This loopback endpoint can be used by a trusted local gateway.

Binding outside loopback is refused unless `ODYSSEY_MCP_ALLOW_REMOTE=1` is explicitly set. A remote deployment must terminate TLS at a trusted proxy or secure MCP tunnel, restrict the public host with `ODYSSEY_MCP_ALLOWED_HOSTS`, protect the token, and preferably add OAuth 2.1 at the gateway. Browser origins are rejected by default; explicitly trusted origins can be listed with `ODYSSEY_MCP_ALLOWED_ORIGINS`. Never expose an unencrypted MCP endpoint directly to the internet.

## Tools

Read-only:

- `odyssey_get_capabilities`
- `odyssey_search_files`
- `odyssey_get_operation_status`

Search results expose `confidence` as `High`, `Medium`, or `Possible`, plus a deterministic `rankingScore` used for ordering. The score is not a probability. Clients should communicate the confidence band and the returned evidence/snippet instead of converting the ranking score to a percentage.

Guarded planning:

- `odyssey_plan_create_folder`
- `odyssey_plan_copy`
- `odyssey_plan_move`
- `odyssey_plan_rename`
- `odyssey_plan_trash`
- `odyssey_plan_write_text`

Execution control:

- `odyssey_execute_approved_operation`
- `odyssey_cancel_operation`

Calling `odyssey_execute_approved_operation` before approval returns the current state and makes no file-system change.

## Access levels

- **Read only** — search and status tools work; every write plan is denied.
- **File management** — writes can be proposed only inside locations indexed by Odyssey; linked paths are blocked.
- **Full access with guards** — paths outside indexed locations can be proposed, but require strong approval. Odyssey data, `/proc`, `/sys`, `/dev`, and equivalent protected boundaries stay blocked.

All write plans expire after ten minutes. Destination conflicts stop rather than overwrite, except an explicit text replacement. Source files and explicit text replacements record their current SHA-256 during planning and abort if the file changes before execution; folder impact is measured again. Path topology is also hashed, so inserting or redirecting a symbolic link after approval aborts execution. Large, destructive, external-target, and system-sensitive operations are escalated to strong approval; linked paths are available only in full-access mode with strong approval.

## Approval flow

1. The AI calls a planning tool.
2. Odyssey normalizes exact paths and runs identity, rate, access, scope, canonical-path, protected-path, conflict, prompt-injection, bulk-impact, and approval guards.
3. A pending plan appears in **Settings → AI and MCP executor**.
4. The user reviews the client, exact operation, risk, impact, and guard findings.
5. Approval changes only the stored plan state; it does not execute anything.
6. The AI calls `odyssey_execute_approved_operation` with the plan ID.
7. Odyssey verifies the plan hash and all file preconditions again, then executes and records the result.

The approval database is shared between the desktop and MCP processes under Odyssey's local application-data directory. The plan content cannot be replaced after approval without invalidating its SHA-256 integrity check.
