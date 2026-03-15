# IranConflictMap.Tools

Admin scripting console. Run from the repo root with AWS credentials in the environment.

## AWS Credentials

The tool uses the default AWS credential chain. If you get an EC2 metadata error, pass your profile explicitly:

```
AWS_PROFILE=your-profile dotnet run --project src/IranConflictMap.Tools -- <command>
```

Or with explicit keys:

```
AWS_ACCESS_KEY_ID=... AWS_SECRET_ACCESS_KEY=... AWS_DEFAULT_REGION=us-east-1 dotnet run --project src/IranConflictMap.Tools -- <command>
```

```
dotnet run --project src/IranConflictMap.Tools -- <command>
```

## Commands

### `reseed`

Clears the `strikes` DynamoDB table and reseeds from all seed files except the last date (which is written by the sync pipeline).

```
dotnet run --project src/IranConflictMap.Tools -- reseed
```

Optionally pass a custom seed directory:

```
dotnet run --project src/IranConflictMap.Tools -- reseed /path/to/seed/criticial-threats
```

**What it does:**
1. Scans and deletes all existing items in `strikes`
2. Loads `seed/criticial-threats/*.json` in date order, skipping the last file
3. BatchWrites all items into `strikes`
