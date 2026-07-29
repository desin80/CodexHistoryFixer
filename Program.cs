using System.Text;

namespace CodexHistoryFixer;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        return TuiApplication.Run(args);
    }
}
