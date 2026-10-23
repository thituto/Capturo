using System.IO;
using System.IO.Pipes;

namespace Capturo.Services;

/// <summary>
/// Linha de comando: "Capturo.exe --action CaptureRegion" (ou --capture, --settings). Se o Capturo já estiver
/// aberto, a nova instância repassa o comando para ele por um pipe nomeado e sai.
/// </summary>
public static class CommandChannel
{
    private static readonly string PipeName = $"Capturo.Commands.{Environment.UserName}";

    /// <summary>Traduz os argumentos num comando: o nome de uma <see cref="HotkeyAction"/> ou "Settings".</summary>
    public static string? Parse(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--capture": return nameof(HotkeyAction.CaptureRegion);
                case "--settings": return "Settings";
                case "--action" when i + 1 < args.Count && Enum.TryParse<HotkeyAction>(args[i + 1], true, out var action):
                    return action.ToString();
            }
        }
        return null;
    }

    /// <summary>Envia o comando para a instância que já está rodando.</summary>
    public static bool Send(string command)
    {
        // Quem foi aberto pelo usuário pode trazer janelas para a frente; repassa esse direito à instância que já roda.
        AllowSetForegroundWindow(ASFW_ANY);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.WriteLine(command);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const int ASFW_ANY = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Escuta comandos de outras instâncias e os entrega em <paramref name="onCommand"/> (numa thread de fundo).</summary>
    public static void Listen(Action<string> onCommand, CancellationToken cancel)
    {
        var thread = new Thread(() =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(cancel).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server);
                    if (reader.ReadLine() is { Length: > 0 } command)
                        onCommand(command.Trim());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    Log.Error("Falha no canal de comandos.", ex);
                }
            }
        })
        { IsBackground = true, Name = "Capturo commands" };
        thread.Start();
    }
}
