using System.Globalization;
using System.Security.Cryptography;

namespace AIVTuber.AuthServer;

/// <summary>
/// Operator commands, run on the server host against the SQLite file:
/// <c>AIVTuber.AuthServer admin --db auth.db &lt;command&gt; [--option value]</c>.
/// Passwords are read from stdin (<c>--password-stdin</c>) or generated and printed once,
/// so they never land in shell history.
/// </summary>
public static class AdminCli
{
    public const string Usage = """
        用法: AIVTuber.AuthServer admin --db <auth.db> <命令> [选项]
          create            --username U --profile P (--days N | --valid-until ISO) [--note T] [--password-stdin]
          set-password      --username U [--password-stdin]      （同时注销该账号全部会话）
          disable | enable  --username U                         （disable 同时注销会话）
          extend            --username U (--days N | --valid-until ISO)
          set-min-revision  --username U --revision N            （拒绝更旧的凭据修订包）
          list
        """;

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr, TimeProvider clock)
    {
        try
        {
            var (command, opts) = Parse(args);
            var dbPath = opts.GetValueOrDefault("db") ?? "auth.db";
            using var store = AuthStore.Open(dbPath);
            var service = new AuthService(store, clock, new AuthServerOptions());
            var now = clock.GetUtcNow();

            switch (command)
            {
                case "create":
                {
                    var username = Required(opts, "username");
                    var (password, generated) = ReadPassword(opts, stdin);
                    var validUntil = ResolveValidUntil(opts, now);
                    var id = service.CreateAccount(username, password, Required(opts, "profile"), validUntil,
                        opts.GetValueOrDefault("note") ?? "");
                    stdout.WriteLine($"created {id} username={username} valid_until={validUntil:O}");
                    if (generated) stdout.WriteLine($"password: {password}");
                    return 0;
                }
                case "set-password":
                {
                    var username = Required(opts, "username");
                    var (password, generated) = ReadPassword(opts, stdin);
                    service.SetPassword(username, password);
                    stdout.WriteLine($"password updated for {username}; sessions revoked");
                    if (generated) stdout.WriteLine($"password: {password}");
                    return 0;
                }
                case "disable":
                case "enable":
                {
                    var username = Required(opts, "username");
                    service.SetEnabled(username, command == "enable");
                    stdout.WriteLine($"{username} {command}d");
                    return 0;
                }
                case "extend":
                {
                    var username = Required(opts, "username");
                    var account = store.FindAccountByUsername(username)
                        ?? throw new InvalidOperationException($"账号 {username} 不存在。");
                    var from = account.ValidUntil > now ? account.ValidUntil : now;
                    var validUntil = ResolveValidUntil(opts, from);
                    service.SetValidUntil(username, validUntil);
                    stdout.WriteLine($"{username} valid_until={validUntil:O}");
                    return 0;
                }
                case "set-min-revision":
                {
                    var username = Required(opts, "username");
                    var revision = int.Parse(Required(opts, "revision"), CultureInfo.InvariantCulture);
                    service.SetMinCredentialRevision(username, revision);
                    stdout.WriteLine($"{username} min_credential_revision={revision}");
                    return 0;
                }
                case "list":
                    foreach (var a in store.ListAccounts())
                        stdout.WriteLine(
                            $"{a.Id}\t{a.Username}\tprofile={a.ProfileId}\tenabled={a.Enabled}\t" +
                            $"valid_until={a.ValidUntil:O}\tmin_rev={a.MinCredentialRevision}\t{a.Note}");
                    return 0;
                default:
                    stderr.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            stderr.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    private static (string Command, Dictionary<string, string> Options) Parse(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string command = "";
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                if (name == "password-stdin") { opts[name] = "true"; continue; }
                if (i + 1 >= args.Length) throw new ArgumentException($"缺少 {arg} 的值。");
                opts[name] = args[++i];
            }
            else if (command.Length == 0) command = arg;
            else throw new ArgumentException($"多余的参数: {arg}");
        }
        return (command, opts);
    }

    private static string Required(Dictionary<string, string> opts, string name) =>
        opts.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ArgumentException($"缺少 --{name}。");

    private static DateTimeOffset ResolveValidUntil(Dictionary<string, string> opts, DateTimeOffset from)
    {
        if (opts.TryGetValue("valid-until", out var iso))
            return DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        if (opts.TryGetValue("days", out var days))
            return from.AddDays(double.Parse(days, CultureInfo.InvariantCulture));
        throw new ArgumentException("需要 --days 或 --valid-until。");
    }

    private static (string Password, bool Generated) ReadPassword(Dictionary<string, string> opts, TextReader stdin)
    {
        if (opts.ContainsKey("password-stdin"))
        {
            var line = stdin.ReadLine();
            if (string.IsNullOrEmpty(line)) throw new ArgumentException("stdin 没有读到密码。");
            return (line, false);
        }
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return (new string(chars), true);
    }
}
