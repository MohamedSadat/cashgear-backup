using CashGear.Backup.Agent;

// Runs as the CashGearBackupAgent Windows service, or as a console app for development.
await AgentHost.Build(args).RunAsync();
