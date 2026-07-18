using KKR.MailLens;

// KKR MailLens CLI. Zaszyfrowany (SQLCipher) lokalny korpus poczty do szybkiego FTS + analityki.
// Sesje trzyma GUI (klucz w RAM); CLI bierze klucz z agenta po named-pipe. 'lock' czysci sesje.
SQLitePCL.Batteries_V2.Init();

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
return cmd switch
{
    "selftest" => SelfTest.Run(),
    "init" => Cli.Init(args),
    "unlock" => Cli.Unlock(args),
    "status" => Cli.Status(),
    "config" => Cli.Config(args),
    "lock" => Cli.Lock(),
    "harvest" => Cli.Harvest(args),
    "query" => Cli.Query(args),
    "query-content" => Cli.QueryContent(args),
    "rebuild-content-index" => Cli.RebuildContentIndex(),
    "stats" => Cli.Stats(),
    "reclassify" => Cli.Reclassify(),
    "analyze" => Cli.Analyze(args),
    "analyze-rules" => Cli.AnalyzeRules(),
    "imap-add" => Cli.ImapAdd(args),
    "imap-list" => Cli.ImapList(),
    "imap-harvest" => Cli.ImapHarvest(args),
    "account" => Cli.Account(args),
    "gmail" => Cli.Gmail(args),
    "processing-status" => Cli.ProcessingStatus(),
    "processing-run" => Cli.ProcessingRun(args),
    "processing-retry" => Cli.ProcessingRetry(),
    "blob-gc" => Cli.BlobGc(args),
    "yubi-test" => Cli.YubiTest(),
    _ => Cli.Help(),
};
