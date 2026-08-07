namespace BcryptHash;

/// <summary>
/// Small console utility to generate BCrypt hashes for the 90-day secret rotation runbook
/// (see docs/runbook-secret-rotation.md). Uses work factor 11 by default, matching ADR-011.
///
/// Usage:
///   dotnet run --project tools/BcryptHash -- --password &lt;value&gt; [--work-factor &lt;n&gt;]
///
/// Example:
///   dotnet run --project tools/BcryptHash -- --password "MyNewPassword123" --work-factor 11
/// </summary>
public static class Program
{
    private const int DefaultWorkFactor = 11;

    public static int Main(string[] args)
    {
        string? password = null;
        var workFactor = DefaultWorkFactor;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--password" when i + 1 < args.Length:
                    password = args[++i];
                    break;

                case "--work-factor" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out workFactor) || workFactor is < 4 or > 31)
                    {
                        Console.Error.WriteLine("Error: --work-factor must be an integer between 4 and 31.");
                        return 2;
                    }

                    break;

                case "--help" or "-h":
                    PrintUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"Error: unknown argument '{args[i]}'.");
                    PrintUsage();
                    return 2;
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Error: --password is required.");
            PrintUsage();
            return 2;
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor);
        Console.WriteLine(hash);
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: BcryptHash --password <value> [--work-factor <n>]");
        Console.Error.WriteLine("  --password       Password to hash (required).");
        Console.Error.WriteLine($"  --work-factor    BCrypt work factor (default: {DefaultWorkFactor}, range: 4-31).");
    }
}
