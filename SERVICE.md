# RenderByte Sync - Windows Service

The RenderByte Sync Agent can be run interactively or installed as a Windows Service to run continuously in the background across user sessions.

## Installation

To install the agent as a Windows Service, you must first configure it using `RenderByte.Sync.Agent.exe configure`.

Once configured, open an elevated (Administrator) command prompt and run:
`RenderByte.Sync.Agent.exe service-install`

This will register a Windows Service named **RenderByteSync** (Display Name: *RenderByte Sync*) set to **Automatic** startup. It runs as the **LocalSystem** account to ensure it can access the DPAPI LocalMachine key and start without a user logon.

## Starting and Stopping

You can control the service using standard Windows tools (like `services.msc` or `sc.exe`) or the built-in commands:

- `RenderByte.Sync.Agent.exe service-start`
- `RenderByte.Sync.Agent.exe service-stop`
- `RenderByte.Sync.Agent.exe service-status`

## Uninstallation

To cleanly stop and remove the Windows Service:
`RenderByte.Sync.Agent.exe service-uninstall`

> [!NOTE]
> Uninstalling the service does **NOT** delete your persistent configuration, secrets, SQLite database, or logs from `C:\ProgramData\RenderByte\Sync`.

## Service Recovery

The service is configured with a failure recovery policy:
- **First Failure:** Restart Service after 60 seconds
- **Second Failure:** Restart Service after 60 seconds
- **Subsequent Failures:** Restart Service after 60 seconds
(Reset failure count after 1 day)

> [!IMPORTANT]
> If the remote Alegon database is temporarily offline, the service will **not** fail or exit. It will remain in the `RUNNING` state and internally use its exponential backoff logic to retry the connection.

## Logs and Operational Status

When running as a service, output is written to:
`C:\ProgramData\RenderByte\Sync\Logs\renderbyte-sync-yyyy-MM-dd.log`
Logs are automatically rotated and retained for 14 days.

## Upgrade Procedure

To upgrade to a new version of RenderByte Sync:
1. `RenderByte.Sync.Agent.exe service-stop`
2. `RenderByte.Sync.Agent.exe service-uninstall`
3. Copy the new version binaries to a new directory (e.g. `RenderByteSync-v0.13`)
4. CD into the new directory.
5. `RenderByte.Sync.Agent.exe service-install`
6. `RenderByte.Sync.Agent.exe service-start`
