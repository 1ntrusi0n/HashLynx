using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HashLynx.Persistence;
using HashLynx.Core;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-smoke");
        Directory.CreateDirectory(output);
        var application = new SmokeApplication(output);
        application.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/HashLynx;component/Themes/AppResources.xaml", UriKind.Relative));
        return application.Run();
    }
}

/// <summary>Loads the real app resources and every real page in an isolated local data directory.</summary>
internal sealed class SmokeApplication(string output) : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Intentionally bypass production composition; the tested views and services are unchanged.
        var errors = new StringBuilder();
        using var listener = new TextWriterTraceListener(new StringWriter(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            var store = new PersistenceStore(new AppPaths(Path.Combine(output, "data-" + Guid.NewGuid().ToString("N"))));
            await store.SaveSettingsAsync(new AppSettings { HashcatDirectory = Path.Combine(output, "deliberately-missing-backend") });
            var services = new AppServices(store);
            var shell = new ShellViewModel(services);
            var window = new MainWindow { DataContext = shell, Width = 1380, Height = 920, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            MainWindow = window;
            window.Show();
            await shell.InitializeAsync();
            Require(services.Installation is null, "An absent backend must not be treated as connected.");
            Require(shell.Selected.Page == shell.Settings, "Missing backend must open Settings.");
            Require(services.Notice.Contains("Settings", StringComparison.Ordinal), "Missing backend must explain setup.");

            foreach (var page in shell.Navigation)
            {
                shell.Selected = page;
                await RenderAsync(window, page.Name.Replace(" / ", "-"));
            }

            shell.Selected = shell.Navigation[0];
            for (var family = 0; family < 4; family++)
            {
                shell.Attack.Family = family;
                shell.Attack.Expert = true;
                await RenderAsync(window, "Attack-family-" + family);
            }
            for (var input = 0; input < 3; input++)
            {
                shell.Attack.Inspector.InputMode = input;
                await RenderAsync(window, "Inspector-input-" + input);
            }

            shell.Attack.Family = 1;
            shell.Attack.Mask = "?d?d?d?d";
            shell.Attack.ProfileName = "Synthetic smoke profile";
            await ((AsyncCommand)shell.Attack.SaveProfileCommand).ExecuteAsync(null);
            Require(shell.Attack.Profiles.Count == 1, "Saving a profile must populate the profile list.");
            Require((await store.LoadProfilesAsync()).Single().Configuration.TargetPath == "", "Profiles must not save target contents.");
            shell.Attack.SelectedProfile = shell.Attack.Profiles[0];
            shell.Attack.Family = 0;
            shell.Attack.LoadProfileCommand.Execute(null);
            Require(shell.Attack.Family == 1 && shell.Attack.Mask == "?d?d?d?d", "Loading a profile must restore attack settings.");
            await ((AsyncCommand)shell.Attack.DeleteProfileCommand).ExecuteAsync(null);
            Require((await store.LoadProfilesAsync()).Count == 0, "Deleting a profile must persist its removal.");

            await ((AsyncCommand)shell.Attack.PreflightCommand).ExecuteAsync(null);
            Require(services.Notice.Contains("Hashcat", StringComparison.Ordinal), "Preflight without backend must explain configuration.");
            var backendPath = Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT");
            if (File.Exists(backendPath)) await CheckConnectedWorkflowAsync(shell, services, window, backendPath);
            foreach (var theme in new[] { "Light", "Dark", "System" })
            {
                AppServices.ApplyTheme(theme);
                window.Width = 1040; window.Height = 700;
                await RenderAsync(window, "Theme-" + theme);
            }
            listener.Flush();
            Require(errors.Length == 0, "WPF binding failures: " + errors);
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: missing-backend startup; all six pages; four attack families; three target modes; three themes; narrow layout; profile save/load/delete; preflight error handling; zero binding errors." + (File.Exists(backendPath) ? " Installed backend: ambiguous identification, mode selection, preflight, and masked/revealed results passed." : ""));
            Console.WriteLine("WPF smoke passed. Screenshots and report: " + output);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            var detail = exception.ToString() + Environment.NewLine + errors;
            await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), detail);
            Console.Error.WriteLine(detail);
            Shutdown(1);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    }

    private async Task CheckConnectedWorkflowAsync(ShellViewModel shell, AppServices services, Window window, string executable)
    {
        await services.ConnectAsync(executable);
        await shell.Attack.Inspector.LoadCatalogAsync();
        var plaintext = "HashLynx-ui-synthetic-" + Guid.NewGuid().ToString("N");
        var digest = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(plaintext)));
        var inspector = shell.Attack.Inspector;
        inspector.InputMode = 0;
        inspector.HashText = digest;
        await inspector.AnalyzeAsync();
        Require(inspector.Matches.Count > 1 && inspector.SelectedMode is null, "Ambiguous targets must require an explicit mode choice.");
        inspector.SelectedMatch = inspector.Matches.Single(mode => mode.Mode == 0);
        Require(inspector.SelectedMode?.Mode == 0, "Selecting an identification result must set the attack mode.");
        shell.Selected = shell.Navigation[0];
        shell.Attack.Family = 1;
        shell.Attack.Mask = "?d?d?d?d";
        await ((AsyncCommand)shell.Attack.PreflightCommand).ExecuteAsync(null);
        Require(shell.Attack.Preflight.StartsWith("Ready to start", StringComparison.Ordinal), "Synthetic mask configuration must pass preflight: " + shell.Attack.Preflight);
        Require(shell.Attack.Preview.Contains("--hash-type 0", StringComparison.Ordinal), "Command preview must contain selected mode.");
        await RenderAsync(window, "Connected-Attack");

        var potfile = Path.Combine(services.Store.Paths.ResultsDirectory, "synthetic.potfile");
        await File.WriteAllTextAsync(potfile, digest + ":" + plaintext + "\n");
        var record = new JobRecord
        {
            Name = "Synthetic result fixture", State = JobState.Cracked,
            Configuration = new HashcatJob { TargetPath = inspector.PreparedTargetPath!, HashMode = 0, Options = new CommonOptions { PotfilePath = potfile } }
        };
        var row = new JobViewModel(record);
        shell.Jobs.Jobs.Add(row);
        var results = (ResultsViewModel)shell.Navigation.Single(page => page.Page is ResultsViewModel).Page;
        results.SelectedJob = row;
        await ((AsyncCommand)results.LoadCommand).ExecuteAsync(null);
        Require(results.Results.Count == 1, "The Results page must load a synthetic recovered result.");
        Require(results.Results[0].Plaintext != plaintext, "Plaintext must initially be hidden.");
        results.Reveal = true;
        Require(results.Results[0].Plaintext == plaintext, "Reveal must display the decoded plaintext.");
        results.Reveal = false;
        shell.Selected = shell.Navigation.Single(page => page.Page == results);
        await RenderAsync(window, "Connected-Results");
        results.SelectedJob = null;
        Require(results.Results.Count == 0, "Switching jobs must clear previous results.");
        inspector.HashText = "changed target";
        Require(inspector.SelectedMode is null && inspector.PreparedTargetPath is null, "Editing a target must invalidate identification and prepared input.");
    }

    private async Task RenderAsync(Window window, string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
