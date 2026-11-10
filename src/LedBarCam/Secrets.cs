using System.Security.Cryptography;
using System.Text;

namespace LedBarCam;

/// <summary>
/// MQTT password, DPAPI-encrypted (LocalMachine scope) in %ProgramData%\LedBarCam\mqtt.pwd.
/// The folder ACL (set by install.ps1) limits read access to SYSTEM, Administrators and
/// NT SERVICE\LedBarCam; DPAPI keeps the value from being readable if the file is copied off-box.
/// The plaintext is never written to disk, logged, or passed on a command line.
/// </summary>
public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LedBarCam.mqtt.v1");

    public static string PasswordFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LedBarCam", "mqtt.pwd");

    public static string ReadPassword()
    {
        var blob = File.ReadAllBytes(PasswordFile);
        var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Interactive: prompts twice without echo and writes the encrypted file.</summary>
    public static int SetPasswordInteractive()
    {
        var a = Prompt("MQTT password: ");
        var b = Prompt("Repeat password: ");
        if (a.Length == 0 || a != b)
        {
            Console.Error.WriteLine("Passwords empty or do not match; nothing written.");
            return 1;
        }
        var plain = Encoding.UTF8.GetBytes(a);
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
            Directory.CreateDirectory(Path.GetDirectoryName(PasswordFile)!);
            File.WriteAllBytes(PasswordFile, blob);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Console.WriteLine($"Saved (encrypted) to {PasswordFile}");
        return 0;
    }

    private static string Prompt(string label)
    {
        Console.Write(label);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
