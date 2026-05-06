using System.Text;
using SwiftlyS2.Core.Natives;

namespace SwiftlyS2.Shared.Misc;

internal class ConsoleRedirector : TextWriter
{
    private readonly TextWriter originalOut;
    private readonly Lock lockObject = new();
    private bool isRedirecting = false;

    public ConsoleRedirector()
    {
        originalOut = Console.Out;
    }

    public override Encoding Encoding => originalOut.Encoding;

    public override void WriteLine(string? value)
    {
        lock (lockObject)
        {
            if (isRedirecting)
            {
                return;
            }

            try
            {
                isRedirecting = true;
                var v = value ?? "(null)";
                if (!v.EndsWith('\n'))
                {
                    v += "\n";
                }

                if (v.Length >= 2048) // maximum console output length per message
                {
                    var offset = 0;
                    while (offset < v.Length)
                    {
                        var length = Math.Min(2048, v.Length - offset);
                        NativeEngineHelpers.SendMessageToConsole(v.Substring(offset, length));
                        offset += length;
                    }
                }
                else
                {
                    NativeEngineHelpers.SendMessageToConsole(v);
                }
            }
            finally
            {
                isRedirecting = false;
            }
        }
    }

    public override void Write(string? value)
    {
        lock (lockObject)
        {
            if (isRedirecting)
            {
                return;
            }

            try
            {
                isRedirecting = true;
                NativeEngineHelpers.SendMessageToConsole(value ?? "(null)");
            }
            finally
            {
                isRedirecting = false;
            }
        }
    }
}