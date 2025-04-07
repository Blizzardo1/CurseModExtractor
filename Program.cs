using System.Reflection;

namespace CurseModExtractor;

internal static class Program {

    private static void HelpFile() {
        string name = $"{Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()?.Location)}.exe";
        Console.WriteLine($"{name} <zipFile>");
    }

    [STAThread]
    private static async Task Main(string[] args) {
        if(args.Length < 1) {
            Console.WriteLine("Specify a Zip File!");
            HelpFile();
            return;
        }
        await ModLoader.Extract(args[0], Directory.Exists("overrides"));
    }
}