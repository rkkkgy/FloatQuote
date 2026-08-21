using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace FloatQuote;

public partial class App : Application
{
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var args = e.Args;
        var headless = args.Contains("--smoke") || args.Contains("--snapshot");
        if (headless)
        {
            if (!AttachConsole(-1))
                AllocConsole();
            Console.OutputEncoding = Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }

        PidFile.Write();
        Exit += (_, _) => PidFile.Remove();

        if (args.Contains("--smoke"))
            Smoke.Prepare();

        var win = new MainWindow();
        MainWindow = win;
        win.Show();

        if (args.Contains("--smoke"))
        {
            Dispatcher.InvokeAsync(async () =>
            {
                var code = await Smoke.RunAsync(win);
                Shutdown(code);
            });
            return;
        }

        var snapIdx = Array.IndexOf(args, "--snapshot");
        if (snapIdx >= 0)
        {
            var outPath = snapIdx + 1 < args.Length ? args[snapIdx + 1] : "snapshot.png";
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(3500);
                win.Left = 80;
                win.Top = 80;
                win.SetExpanded(false);
                await Task.Delay(400);
                var dir = ConfigStore.AppDir;
                Snapshot.Save(win, Path.Combine(dir, "qa_bar.png"));

                win.SetExpanded(true);
                await Task.Delay(600);
                Snapshot.Save(win, Path.Combine(dir, "qa_expanded.png"));

                var names = win.Quotes
                    .Where(kv => !string.IsNullOrEmpty(kv.Value.Name) && kv.Value.Name != "--")
                    .ToDictionary(kv => kv.Key, kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase);
                var dlg = new StockEditDialog(win.Stocks, names)
                {
                    Owner = win,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 80,
                    Top = 280,
                    Width = 420,
                    Height = 400,
                };
                dlg.Show();
                await Task.Delay(500);
                Snapshot.Save(dlg, Path.Combine(dir, "qa_dialog.png"));
                dlg.Close();

                File.WriteAllText(Path.Combine(dir, "qa_log.txt"),
                    $"saved bar/expanded/dialog under {dir}\nexpanded={win.Width}x{win.Height}\n");
                Shutdown(0);
            });
        }
    }
}
