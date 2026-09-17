## Agent skills

### Issue tracker

Issues and specs are tracked in this repository's GitHub Issues. See `docs/agents/issue-tracker.md`.

### Domain docs

This is a single-context repository. See `docs/agents/domain.md`.

## After every commit

Rebuild the Game Peer in Release so `src/Multimnesia.Client/bin/Release/net10.0/` matches the commit:

```powershell
dotnet build .\src\Multimnesia.Client\Multimnesia.Client.csproj --configuration Release
```

The maintainer copies that folder to the second LAN PC for manual Multiplayer Session tests; a stale build there makes a test fail for reasons unrelated to the commit.
