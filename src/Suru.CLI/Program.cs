using Suru.CLI;

return Command
    .From(Spec.Parse(args))
    .Execute();
