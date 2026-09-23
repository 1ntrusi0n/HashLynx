using System.Text.Json;

// Test-only process fixture: no shell, backend, network or private inputs.
if (args.Length == 0) return 64;
switch (args[0])
{
    case "echo":
        Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));
        return 0;
    case "emit":
        Console.WriteLine("synthetic-private-hash:synthetic-private-plaintext");
        Console.WriteLine("{\"status\":3,\"progress\":[40,100],\"recovered_hashes\":[0,1],\"devices\":[{\"device_id\":2,\"speed\":9876,\"temp\":57,\"util\":99,\"guess_candidates\":\"synthetic-private-candidate\"}]}");
        Console.Error.WriteLine("Hashfile 'synthetic-private-target': Token length exception: synthetic-private-hash");
        Console.Error.WriteLine("synthetic-private-plaintext");
        return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    case "wait":
        await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine("{\"status\":3,\"progress\":[0,100]}");
        Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    default:
        return 64;
}
