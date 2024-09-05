namespace ProxyServer;

internal class Program
{
    static void Main(string[] args)
    {
        var prefixes = CommandLine.Current.GetNullifiedArgument(0).SplitToNullifiedList([',']).ToArray();
        if (prefixes.Length == 0)
        {
            Help();
            return;
        }

        using var server = new Server(prefixes);
        var uri = CommandLine.Current.GetNullifiedArgument(1);
        if (uri != null)
        {
            server.TargetUrl = new Uri(uri);
        }

        server.Start();
        do
        {
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Escape)
                break;
        }
        while (true);
    }

    static void Help()
    {
        Console.WriteLine("Format is ProxyServer <uri> <prefix1,prefix2,...prefixN>");
    }
}
