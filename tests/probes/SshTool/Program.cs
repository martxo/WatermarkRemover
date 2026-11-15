// Minimal scriptable SSH/SCP helper — the deploy tool for the Tencent Cloud update mirror.
// Windows OpenSSH cannot take a password non-interactively (no SSH_ASKPASS support), so this
// wraps SSH.NET for the one-time key bootstrap; native ssh/scp with the key takes over after.
//
//   SshTool exec     <host> <user> <pass>  "<remote command>"
//   SshTool exec-key <host> <user> <key>   "<remote command>"
//   SshTool scp      <host> <user> <pass>  <localPath> <remoteDir>
using System.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Sftp;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: exec|exec-key|scp <host> <user> <passOrKey> [localPath] <remote>");
    return 2;
}

string mode = args[0];
string host = args[1];
string user = args[2];
string secret = args[3];

ConnectionInfo Connect()
{
    if (mode == "exec-key")
    {
        return new ConnectionInfo(host, user, new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(secret)));
    }
    return new ConnectionInfo(host, user,
        new PasswordAuthenticationMethod(user, secret),
        new PrivateKeyAuthenticationMethod(user)); // keyboard-interactive fallback
}

using var client = new SshClient(Connect());
client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
client.Connect();
Console.Error.WriteLine($"connected to {host} as {user}");

if (mode is "exec" or "exec-key")
{
    string command = args.Length > 4 ? string.Join(' ', args[4..]) : string.Empty;
    using var cmd = client.CreateCommand(command);
    cmd.CommandTimeout = TimeSpan.FromMinutes(30);
    string result = cmd.Execute();
    Console.Write(result);
    Console.Error.WriteLine($"[exit={cmd.ExitStatus ?? -1}]");
    return cmd.ExitStatus ?? -1;
}

if (mode == "scp")
{
    // scp <host> <user> <pass> <localPath> <remoteDir>
    if (args.Length < 6)
    {
        Console.Error.WriteLine("scp needs <localPath> <remoteDir>");
        return 2;
    }
    string local = args[4];
    string remoteDir = args[5];
    ConnectionInfo info = client.ConnectionInfo;
    client.Disconnect();

    using var sftp = new SftpClient(info);
    sftp.Connect();
    sftp.ChangeDirectory("/");
    EnsureDir(sftp, remoteDir);

    if (Directory.Exists(local))
    {
        foreach (string file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(local, file).Replace('\\', '/');
            string target = $"{remoteDir}/{relative}";
            EnsureDir(sftp, target[..target.LastIndexOf('/')]);
            using var fs = File.OpenRead(file);
            sftp.UploadFile(fs, target, true);
            Console.Error.WriteLine($"[uploaded {relative} {fs.Length}B]");
        }
    }
    else
    {
        using var fs = File.OpenRead(local);
        sftp.UploadFile(fs, $"{remoteDir}/{Path.GetFileName(local)}", true);
        Console.Error.WriteLine($"[uploaded {Path.GetFileName(local)} {fs.Length}B]");
    }
    return 0;
}

/// <summary>mkdir -p equivalent over SFTP (no native recursive mkdir in the protocol).</summary>
static void EnsureDir(SftpClient sftp, string path)
{
    string current = string.Empty;
    foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        current += "/" + part;
        try { sftp.CreateDirectory(current); } catch (Exception) { /* exists */ }
    }
}

Console.Error.WriteLine($"unknown mode {mode}");
return 2;
