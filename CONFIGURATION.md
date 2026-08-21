# Configuration & Secrets Management (M11)

RenderByte Sync supports persistent configuration and DPAPI-encrypted secrets to simplify execution without needing manual environment variables on every run.

## Default Locations
By default, the Agent looks for the following files:
- **Configuration**: `C:\ProgramData\RenderByte\Sync\config.json`
- **Secrets**: `C:\ProgramData\RenderByte\Sync\secrets.json`

## Configuration Precedence
The agent resolves configuration in the following order (highest to lowest):
1. **Environment Variables**: `RENDERBYTE_ALEGON_CONNECTION_STRING`, `RENDERBYTE_SYNC_SOURCE_ID`, etc. (Overrides any persistent file).
2. **Persistent Config/Secrets**: Loaded from `config.json` and `secrets.json`.
3. **Defaults**: Used for intervals if missing in both env and config files.

## Setting Up (Interactive)
Run the following command to interactively set up a new persistent configuration:
```bash
RenderByte.Sync.Agent.exe configure
```
You will be prompted to enter the SQL credentials, API URL, and Source ID. Passwords will be masked.

## Validating Configuration
To safely verify if the current configuration is structurally correct and DPAPI decryption works, run:
```bash
RenderByte.Sync.Agent.exe config-check
```
This will NOT output any secrets to the screen.

## Moving Installations
**IMPORTANT**: The secrets are encrypted using Windows DPAPI `LocalMachine` scope. This ciphertext is NOT portable to another PC.
If you move the installation to a new PC, you MUST run `configure` again on the new machine to generate new DPAPI ciphertext for that machine's trust boundary.
