# IranConflictMap.Tools

Admin scripting console. Run from the repo root.

```
dotnet run --project src/IranConflictMap.Tools -- [--profile <profile>] [--region <region>] <command> [options]
```

## AWS Credentials

The tool uses the default AWS credential chain. If you get an EC2 metadata error, pass your profile explicitly:

```
dotnet run --project src/IranConflictMap.Tools -- --profile default --region us-east-1 reseed
```

## Commands

### `reseed`

Clears the `strikes` DynamoDB table and reseeds by sending all seed files through the processor SQS queue (skips the last seed date — written by the sync pipeline).

```
dotnet run --project src/IranConflictMap.Tools -- --profile default reseed
```

Optionally pass a custom seed directory:

```
dotnet run --project src/IranConflictMap.Tools -- --profile default reseed /path/to/seed/criticial-threats
```

**What it does:**
1. Scans and deletes all existing items in `strikes`
2. Loads `seed/criticial-threats/*.json` in date order, skipping the last file
3. Sends each file as a `SyncEnvelope` message to the FIFO processor queue
4. The Processor Lambda handles writes and stamps `created_at` on all new events

Messages are sent to the `sync` FIFO group so they are processed in order.
