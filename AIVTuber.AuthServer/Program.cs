using AIVTuber.AuthServer;

if (args.Length > 0 && args[0] == "admin")
    return AdminCli.Run(args[1..], Console.In, Console.Out, Console.Error, TimeProvider.System);

var app = AuthServerApp.Build(args);
app.Run();
return 0;
