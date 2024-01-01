using System.CommandLine.Parsing;

namespace Ilyfairy.Tools;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            args = ["--help"];
        }

        using DstDownloader dst = new();
        ApplicationCommand command = new(dst);
        command.Build();

        await command.Parser.InvokeAsync(args);

        return command.ExitCode;
    }
}
