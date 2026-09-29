using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace VideoEditor.App;

public partial class MainWindow : Window
{
    public const string AppHost = "app.vedit";
    public const string ProjectHost = "project.vedit";

    private readonly EditorHost _host;
    private readonly string? _startupProject;
    private ProjectFileServer? _files;
    private string? _pendingRoot;

    public MainWindow(string? startupProject)
    {
        InitializeComponent();
        _startupProject = startupProject;
        _host = new EditorHost(this);
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += (_, e) => e.Cancel = !_host.ConfirmClose();
        Closed += (_, _) => _host.Dispose();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoEditor", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir,
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("Manca il runtime WebView2. Installalo da https://developer.microsoft.com/microsoft-edge/webview2/",
                "Video Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        // Strumenti di sviluppo sempre disponibili (F12 dall'editor) per la diagnosi.
        core.Settings.AreDevToolsEnabled = true;
        core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Allow);
        _files = new ProjectFileServer(core, ProjectHost) { RootDirectory = _pendingRoot };
        _files.Attach();
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
        core.Navigate($"https://{AppHost}/index.html");
    }

    /// <summary>Mappa la cartella del progetto sull'host virtuale usato per proxy, anteprime e fotogrammi.</summary>
    public void MapProjectFolder(string directory)
    {
        // Niente SetVirtualHostNameToFolderMapping: aggiunto dopo il caricamento della pagina
        // non sempre vale per i video. I file li serve ProjectFileServer.
        _pendingRoot = directory;
        if (_files is not null) _files.RootDirectory = directory;
    }

    public void OpenDevTools() => Web.CoreWebView2?.OpenDevToolsWindow();

    /// <summary>Invia un messaggio alla pagina (thread-safe).</summary>
    public void Post(JsonObject message)
    {
        var json = message.ToJsonString(VideoEditor.Core.Model.Json.Compact);
        Dispatcher.InvokeAsync(() => Web.CoreWebView2?.PostWebMessageAsJson(json));
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (msg is null) return;

        // File trascinati da Esplora risorse: il WebView2 ne fornisce il percorso.
        var files = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToList();
        if (msg["type"]?.GetValue<string>() == "ready" && _startupProject is not null)
        {
            await _host.HandleAsync(msg, files);
            await _host.OpenProjectAsync(_startupProject);
            return;
        }
        await _host.HandleAsync(msg, files);
    }
}
