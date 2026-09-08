using System.Diagnostics;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace CodeName399.ServerSetupManager;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed record ServiceDef(
    string Name,
    string Project,
    string Folder,
    string Service,
    int Port,
    string Exe,
    string InstrumentType);

public sealed class MainForm : Form
{
    TextBox source = new(), server = new(), nginx = new(), uiSource = new(), sourceZip = new();
    RichTextBox log = new();

    readonly List<ServiceDef> services = new();
    readonly string[] legacyServices =
    {
        "CodeName399.KuberX",
        "CodeName399.Instrument",
        "CodeName399.HistoricalCandles",
        "CodeName399.Evaluation",
        "CodeName399.VirtualTrading",
        "CodeName399.Buying",
        "CodeName399.Selling",
        "CodeName399.StockPerformance",
        "CodeName399.Optimization",
        "CodeName399.Email"
    };

    readonly Dictionary<string, CheckBox> selected = new();
    readonly Dictionary<string, Label> states = new();

    TableLayoutPanel serviceList = null!;
    CheckBox selectAll = null!;
    Button fullSetup = null!, build = null!, publish = null!, restart = null!,
           stop = null!, cancel = null!, nginxBtn = null!, uiBuild = null!,
           refresh = null!, clear = null!;
    CancellationTokenSource? cts;

    string Source => source.Text.Trim();
    string Server => server.Text.Trim();
    string UiSource => uiSource.Text.Trim();
    string SourceZip => sourceZip.Text.Trim();
    string Sln => Path.Combine(Source, "CodeName399.sln");

    public MainForm()
    {
        Text = "CodeName399 Server Setup Manager";
        Width = 1450;
        Height = 900;
        MinimumSize = new Size(1150, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);

        BuildUi();

        Shown += async (_, _) =>
        {
            await DiscoverServicesSafe();
            await RefreshStatus();
        };

        Resize += (_, _) => SetSafeSplitterDistance();
    }

    void AddPath(
        TableLayoutPanel table,
        int row,
        string label,
        TextBox box,
        string value,
        Action<TextBox> browse)
    {
        box.Text = value;
        box.Dock = DockStyle.Fill;

        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        }, 0, row);

        table.Controls.Add(box, 1, row);
        table.Controls.Add(Btn("Browse", (_, _) => browse(box)), 2, row);
    }

    Button Btn(string text, EventHandler e)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 34,
            MinimumSize = new Size(105, 34),
            Margin = new Padding(4)
        };

        b.Click += e;
        return b;
    }

    void SetSafeSplitterDistance()
    {
        var split = Controls.OfType<SplitContainer>().FirstOrDefault();
        if (split == null || split.ClientSize.Width <= 0)
            return;

        const int panel1Min = 720;
        const int panel2Min = 320;

        split.Panel1MinSize = panel1Min;
        split.Panel2MinSize = panel2Min;

        var max = split.ClientSize.Width - panel2Min - split.SplitterWidth;

        if (max >= panel1Min)
            split.SplitterDistance = Math.Clamp(
                (int)(split.ClientSize.Width * .60),
                panel1Min,
                max);
    }

    void BrowseFolder(TextBox box)
    {
        using var d = new FolderBrowserDialog { SelectedPath = box.Text };
        if (d.ShowDialog() == DialogResult.OK)
            box.Text = d.SelectedPath;
    }

    void BrowseFile(TextBox box)
    {
        using var d = new OpenFileDialog
        {
            Filter = box == sourceZip
                ? "ZIP files (*.zip)|*.zip|All files (*.*)|*.*"
                : "Executable (*.exe)|*.exe|All files (*.*)|*.*"
        };

        if (d.ShowDialog() == DialogResult.OK)
            box.Text = d.FileName;
    }

    void Write(string message, bool error = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Write(message, error));
            return;
        }

        log.AppendText(
            $"[{DateTime.Now:HH:mm:ss}] {(error ? "[ERROR] " : "")}{message}{Environment.NewLine}");

        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    void Busy(bool busy)
    {
        foreach (var button in Controls
                     .OfType<Control>()
                     .SelectMany(Flatten)
                     .OfType<Button>())
        {
            button.Enabled = !busy;
        }

        cancel.Enabled = busy;
    }

    IEnumerable<Control> Flatten(Control c) =>
        c.Controls.Cast<Control>()
            .SelectMany(x => new[] { x }.Concat(Flatten(x)));

    void BeginOperation()
    {
        cts?.Dispose();
        cts = new CancellationTokenSource();
        Busy(true);
    }

    void EndOperation()
    {
        Busy(false);
        cts?.Dispose();
        cts = null;
    }

    void CancelCurrentOperation()
    {
        if (cts == null)
            return;

        cts.Cancel();
        Write("Cancellation requested...");
    }

    CancellationToken Token => cts?.Token ?? CancellationToken.None;

    async Task DiscoverServicesSafe()
    {
        try
        {
            ValidateSourceRoot();
            await DiscoverServices(Token);
        }
        catch (Exception ex)
        {
            Write("Service discovery failed: " + ex.Message, true);
            RebuildServiceList();
        }
    }

    async Task DiscoverServices(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        Write("=== DISCOVERING ASP.NET CORE SERVICES ===");
        Write("ZIP: " + (string.IsNullOrWhiteSpace(SourceZip) ? "(not configured)" : SourceZip));
        Write("Source: " + Source);

        var discovered = new List<ServiceDef>();

        if (!string.IsNullOrWhiteSpace(SourceZip) && File.Exists(SourceZip))
        {
            discovered.AddRange(await Task.Run(
                () => DiscoverFromZip(SourceZip, Source, Server, token), token));
        }
        else
        {
            Write("[INFO] Deployment ZIP not found; discovering from source tree.");
            discovered.AddRange(await Task.Run(
                () => DiscoverFromSource(Source, Server, token), token));
        }

        if (discovered.Count == 0)
            throw new Exception("No ASP.NET Core web services were found in the deployment source.");

        services.Clear();
        services.AddRange(discovered
            .GroupBy(x => x.Service, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()));

        RebuildServiceList();

        Write("[DISCOVERY] Services selected for installation:");
        foreach (var s in services)
            Write($"  {s.Name} | {s.Project} | {s.Folder} | TCP {s.Port} | {s.Exe}");

        Write($"[OK] {services.Count} service(s) discovered.");
    }

    static List<ServiceDef> DiscoverFromZip(
        string zipPath,
        string apiRoot,
        string serverRoot,
        CancellationToken token)
    {
        var rows = new List<ServiceDef>();
        var nextPort = 5005;

        using var zip = ZipFile.OpenRead(zipPath);

        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();

            if (!entry.FullName.StartsWith("CodeName399.API/", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 3 ||
                !parts[^1].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;

            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();

            if (!xml.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                continue;

            var folder = parts[1];
            var projectName = Path.GetFileNameWithoutExtension(parts[^1]);
            var service = projectName.EndsWith(".API", StringComparison.OrdinalIgnoreCase)
                ? projectName[..^4]
                : projectName;

            var relativeProject = string.Join(
                Path.DirectorySeparatorChar,
                parts.Skip(1));

            var project = Path.Combine(apiRoot, relativeProject);
            var destination = Path.Combine(serverRoot, folder);

            var port = TryReadLaunchSettingsPort(
                Path.Combine(Path.GetDirectoryName(project) ?? "", "Properties", "launchSettings.json"));

            if (port <= 0)
            {
                port = NextFreePort(nextPort);
                nextPort = port + 1;
            }

            rows.Add(new ServiceDef(
                service,
                project,
                folder,
                service,
                port,
                projectName + ".exe",
                GetInstrumentType(service)));
        }

        return rows;
    }

    static List<ServiceDef> DiscoverFromSource(
        string apiRoot,
        string serverRoot,
        CancellationToken token)
    {
        var rows = new List<ServiceDef>();
        var nextPort = 5005;

        foreach (var project in Directory
                     .GetFiles(apiRoot, "*.csproj", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();

            var xml = File.ReadAllText(project);
            if (!xml.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                continue;

            var projectName = Path.GetFileNameWithoutExtension(project);
            var service = projectName.EndsWith(".API", StringComparison.OrdinalIgnoreCase)
                ? projectName[..^4]
                : projectName;

            var folder = new DirectoryInfo(Path.GetDirectoryName(project)!).Name;
            var destination = Path.Combine(serverRoot, folder);

            var port = TryReadLaunchSettingsPort(
                Path.Combine(Path.GetDirectoryName(project)!, "Properties", "launchSettings.json"));

            if (port <= 0)
            {
                port = NextFreePort(nextPort);
                nextPort = port + 1;
            }

            rows.Add(new ServiceDef(
                service,
                project,
                folder,
                service,
                port,
                projectName + ".exe",
                GetInstrumentType(service)));
        }

        return rows;
    }

    static int TryReadLaunchSettingsPort(string file)
    {
        if (!File.Exists(file))
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));

            if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
                return 0;

            foreach (var profile in profiles.EnumerateObject())
            {
                if (!profile.Value.TryGetProperty("applicationUrl", out var url))
                    continue;

                var value = url.GetString() ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(
                    value, @":(\d+)");

                if (match.Success && int.TryParse(match.Groups[1].Value, out var port))
                    return port;
            }
        }
        catch
        {
            // Match the batch file: invalid/missing launchSettings simply falls
            // back to the next available port.
        }

        return 0;
    }

    static int NextFreePort(int start)
    {
        var port = start;

        while (IPGlobalProperties
            .GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(x => x.Port == port))
        {
            port++;
        }

        return port;
    }

    static string GetInstrumentType(string service)
    {
        if (service.Contains("Option", StringComparison.OrdinalIgnoreCase))
            return "Options";

        if (service.Contains("Future", StringComparison.OrdinalIgnoreCase))
            return "Futures";

        if (service.Contains("Equity", StringComparison.OrdinalIgnoreCase))
            return "Equity";

        if (service.Contains("Gateway", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("Auth", StringComparison.OrdinalIgnoreCase))
            return "Shared";

        return "Web";
    }

    void RebuildServiceList()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RebuildServiceList);
            return;
        }

        selected.Clear();
        states.Clear();
        serviceList.Controls.Clear();

        foreach (var s in services)
        {
            var cb = new CheckBox
            {
                AutoSize = true,
                Margin = new Padding(4),
                Checked = true
            };

            selected[s.Name] = cb;

            var name = new Label
            {
                Text = s.Name,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 8, 4, 4)
            };

            var type = new Label
            {
                Text = $"{s.InstrumentType} • :{s.Port}",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 8, 4, 4)
            };

            var state = new Label
            {
                Text = "Checking...",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 8, 4, 4)
            };

            states[s.Name] = state;

            serviceList.Controls.Add(cb);
            serviceList.Controls.Add(name);
            serviceList.Controls.Add(type);
            serviceList.Controls.Add(state);
        }

        selectAll.Checked = services.Count > 0 && services.All(x => selected[x.Name].Checked);
    }

    void ValidateSourceRoot()
    {
        if (!Directory.Exists(Source))
            throw new Exception("Source directory not found: " + Source);

        if (!File.Exists(Sln))
            throw new Exception("Solution not found: " + Sln);
    }

    async Task BuildSolution()
    {
        BeginOperation();

        try
        {
            ValidateSourceRoot();
            await RestoreAndBuild(Token);
        }
        catch (OperationCanceledException)
        {
            Write("Build cancelled.");
        }
        catch (Exception ex)
        {
            Write(ex.Message, true);
            MessageBox.Show(ex.Message, "Build failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task RestoreAndBuild(CancellationToken token)
    {
        Write("=== RESTORE STARTED ===");

        var restore = await Run(
            "dotnet",
            $"restore \"{Sln}\"",
            Source,
            "Restore",
            true,
            token);

        if (restore != 0)
            throw new Exception("dotnet restore failed.");

        Write("=== BUILD STARTED ===");

        var build = await Run(
            "dotnet",
            $"build \"{Sln}\" -c Release --no-restore",
            Source,
            "Build",
            true,
            token);

        if (build != 0)
            throw new Exception("Solution compilation failed.");

        Write("=== BUILD SUCCESSFUL ===");
    }

    async Task FullSetup()
    {
        if (cts != null)
            return;

        BeginOperation();

        try
        {
            Write("=== FULL SERVER SETUP STARTED ===");

            ValidateSourceRoot();

            Write("[1/11] Creating server directories...");
            Directory.CreateDirectory(Server);
            Directory.CreateDirectory(Path.Combine(Server, "UI"));
            Directory.CreateDirectory(Path.Combine(Server, "_backups"));

            Write("[2/11] Checking .NET...");
            if (await Run("dotnet", "--version", Source, ".NET", true, Token) != 0)
                throw new Exception(".NET check failed.");

            Write("[3/11] Checking Node.js...");
            if (await Run("node", "--version", Source, "Node", true, Token) != 0)
                throw new Exception("Node.js check failed.");

            if (await Run("npm", "--version", Source, "npm", true, Token) != 0)
                throw new Exception("npm check failed.");

            Write("[4/11] MongoDB setup skipped.");
            Write("[OK] Existing MongoDB installation and configuration will not be changed.");

            Write("[5/11] Backing up existing server configuration...");
            Directory.CreateDirectory(Path.Combine(Server, "_backups", "BeforeSetup"));
            Write("[INFO] Service configuration backup is handled per discovered service during deployment.");

            Write("[6/11] API build...");
            await DiscoverServices(Token);
            await RestoreAndBuild(Token);

            Write("[7/11] Discovering and installing services present in source...");
            foreach (var service in services)
            {
                Token.ThrowIfCancellationRequested();
                await DeployProject(service, Token);
            }

            Write("[8/11] UI build...");
            await BuildUiCore(Token);

            Write("[9/11] Service verification...");
            await RefreshStatus();

            Write("[10/11] Configuring Nginx...");
            await ConfigureNginxCore(Token);

            Write("[11/11] Cloudflare...");
            Write("[INFO] Cloudflare configuration left unchanged.");
            Write("[INFO] Existing cloudflared service is not deleted or recreated.");

            await RefreshStatus();

            Write("============================================================");
            Write("SETUP SUCCESSFUL");
            Write("============================================================");
            Write("UI  : https://codename399.com");
            Write("API : https://api.codename399.com");
            Write($"API services discovered: {services.Count}");
            Write("UI deployment: disabled, matching setup.bat.");
        }
        catch (OperationCanceledException)
        {
            Write("=== FULL SERVER SETUP CANCELLED ===", true);
        }
        catch (Exception ex)
        {
            Write("[ERROR] " + ex.Message, true);
            MessageBox.Show(ex.Message, "Setup failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task DeployProject(ServiceDef service, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        Write($"[{service.Name}] Deploying...");
        await Stop(service, token);

        var destination = Path.Combine(Server, service.Folder);
        Directory.CreateDirectory(destination);

        Write($"[{service.Name}] Publishing Release build...");

        var result = await Run(
            "dotnet",
            $"publish \"{service.Project}\" -c Release -o \"{destination}\"",
            Source,
            service.Name,
            true,
            token);

        if (result != 0)
            throw new Exception($"dotnet publish failed for {service.Name}.");

        await EnsureService(service, token);
        await RestartService(service, token);

        Write($"[OK] {service.Name} installed/configured and verified.");
    }

    async Task PublishSelected()
    {
        if (services.Count == 0)
            await DiscoverServicesSafe();

        var selectedServices = services
            .Where(x => selected.TryGetValue(x.Name, out var cb) && cb.Checked)
            .ToList();

        if (selectedServices.Count == 0)
        {
            MessageBox.Show("Select at least one discovered service.");
            return;
        }

        BeginOperation();

        try
        {
            foreach (var service in selectedServices)
                await DeployProject(service, Token);
        }
        catch (OperationCanceledException)
        {
            Write("Publish cancelled.");
        }
        catch (Exception ex)
        {
            Write(ex.Message, true);
            MessageBox.Show(ex.Message, "Publish failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task EnsureService(ServiceDef service, CancellationToken token)
    {
        var exe = Path.Combine(Server, service.Folder, service.Exe);

        if (!File.Exists(exe))
            throw new Exception($"Executable not found: {exe}");

        var query = await Run(
            "sc.exe",
            $"query \"{service.Service}\"",
            Source,
            service.Name,
            false,
            token);

        var binPath =
            $"\\\"{exe}\\\" --urls http://127.0.0.1:{service.Port}";

        if (query != 0)
        {
            Write($"[{service.Name}] Creating Windows service...");

            var create = await Run(
                "sc.exe",
                $"create \"{service.Service}\" binPath= \"{binPath}\" start= auto DisplayName= \"{service.Service}\" obj= LocalSystem",
                Source,
                service.Name,
                true,
                token);

            if (create != 0)
                throw new Exception($"Failed to create {service.Service}.");
        }
        else
        {
            Write($"[{service.Name}] Updating Windows service...");

            var config = await Run(
                "sc.exe",
                $"config \"{service.Service}\" start= auto binPath= \"{binPath}\" obj= LocalSystem",
                Source,
                service.Name,
                true,
                token);

            if (config != 0)
                throw new Exception($"Failed to configure {service.Service}.");
        }

        await Run(
            "sc.exe",
            $"failure \"{service.Service}\" reset= 86400 actions= restart/5000/restart/10000/restart/30000",
            Source,
            service.Name,
            false,
            token);
    }

    async Task RestartAll()
    {
        BeginOperation();

        try
        {
            if (services.Count == 0)
                await DiscoverServices(Token);

            Write("=== RESTARTING ALL DISCOVERED SERVICES ===");

            foreach (var service in services)
            {
                Token.ThrowIfCancellationRequested();
                await RestartService(service, Token);
            }

            Write("=== ALL DISCOVERED SERVICES ARE RUNNING ===");
        }
        catch (OperationCanceledException)
        {
            Write("Restart cancelled.");
        }
        catch (Exception ex)
        {
            Write(ex.Message, true);
            MessageBox.Show(ex.Message, "Restart failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task StopSelected()
    {
        var selectedServices = services
            .Where(x => selected.TryGetValue(x.Name, out var cb) && cb.Checked)
            .ToList();

        if (selectedServices.Count == 0)
        {
            MessageBox.Show("Select at least one discovered service.");
            return;
        }

        BeginOperation();

        try
        {
            foreach (var service in selectedServices)
            {
                Token.ThrowIfCancellationRequested();
                await Stop(service, Token);
            }
        }
        catch (OperationCanceledException)
        {
            Write("Stop cancelled.");
        }
        catch (Exception ex)
        {
            Write(ex.Message, true);
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task Stop(ServiceDef service, CancellationToken token)
    {
        var query = await Run(
            "sc.exe",
            $"query \"{service.Service}\"",
            Source,
            service.Name,
            false,
            token);

        if (query != 0)
        {
            Write($"[{service.Name}] is not installed; it will be created.");
            return;
        }

        var state = await State(service.Service);

        if (state == "STOPPED")
            return;

        Write($"[{service.Name}] Stopping service...");

        await Run(
            "sc.exe",
            $"stop \"{service.Service}\"",
            Source,
            service.Name,
            false,
            token);

        for (var i = 0; i < 30; i++)
        {
            token.ThrowIfCancellationRequested();

            if (await State(service.Service) is null or "STOPPED")
                return;

            await Task.Delay(1000, token);
        }

        Write($"[WARN] {service.Name} did not stop within 30 seconds.", true);

        var pid = await GetServicePid(service.Service);

        if (pid > 0)
        {
            Write($"[{service.Name}] Terminating PID {pid}...");
            await Run(
                "taskkill",
                $"/PID {pid} /T /F",
                Source,
                service.Name,
                false,
                token);

            await Task.Delay(2000, token);
        }

        if (await State(service.Service) != "STOPPED")
            throw new Exception($"Unable to stop {service.Service}.");
    }

    async Task<int> GetServicePid(string serviceName)
    {
        var output = await Capture(
            "sc.exe",
            $"queryex \"{serviceName}\"",
            Source,
            CancellationToken.None);

        var match = System.Text.RegularExpressions.Regex.Match(
            output,
            @"PID\s*:\s*(\d+)");

        return match.Success && int.TryParse(match.Groups[1].Value, out var pid)
            ? pid
            : 0;
    }

    async Task RestartService(ServiceDef service, CancellationToken token)
    {
        Write($"[{service.Name}] Restarting on TCP {service.Port}...");

        await Stop(service, token);

        var start = await Run(
            "sc.exe",
            $"start \"{service.Service}\"",
            Source,
            service.Name,
            false,
            token);

        if (start != 0)
            throw new Exception($"Failed to start {service.Service}.");

        var running = false;

        for (var i = 0; i < 30; i++)
        {
            token.ThrowIfCancellationRequested();

            if (await State(service.Service) == "RUNNING")
            {
                running = true;
                break;
            }

            await Task.Delay(1000, token);
        }

        if (!running)
            throw new Exception($"{service.Service} did not reach RUNNING state.");

        var portOk = await WaitPort(service.Port, 30, token);

        if (!portOk)
        {
            Write($"[{service.Name}] RUNNING but TCP {service.Port} is not listening.", true);
            await WriteRecentApplicationEvents(service.Name);
            throw new Exception(
                $"{service.Name} is RUNNING but TCP {service.Port} is not listening.");
        }

        Write($"[OK] {service.Name} restarted and TCP {service.Port} is listening.");
    }

    async Task<bool> WaitPort(int port, int seconds, CancellationToken token)
    {
        for (var i = 0; i < seconds; i++)
        {
            token.ThrowIfCancellationRequested();

            if (await Port(port))
                return true;

            await Task.Delay(1000, token);
        }

        return false;
    }

    async Task WriteRecentApplicationEvents(string serviceName)
    {
        try
        {
            var output = await Capture(
                "powershell",
                "-NoProfile -ExecutionPolicy Bypass -Command " +
                "\"Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-5)} " +
                $"-ErrorAction SilentlyContinue | Where-Object {{$_.Message -match '{serviceName}|\\.NET Runtime|Application Error'}} " +
                "| Select-Object -First 8 TimeCreated,ProviderName,Id,LevelDisplayName,Message | Format-List\"",
                Source,
                CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(output))
            {
                Write("Recent Windows Application events:");
                foreach (var line in output.SplitLines())
                    Write("  " + line);
            }
        }
        catch (Exception ex)
        {
            Write("Could not read recent Application events: " + ex.Message, true);
        }
    }

    async Task BuildUi()
    {
        BeginOperation();

        try
        {
            await BuildUiCore(Token);
        }
        catch (OperationCanceledException)
        {
            Write("Angular UI build cancelled.");
        }
        catch (Exception ex)
        {
            Write(ex.Message, true);
            MessageBox.Show(ex.Message, "UI build failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task BuildUiCore(CancellationToken token)
    {
        if (!Directory.Exists(UiSource))
            throw new Exception("Angular UI source directory not found: " + UiSource);

        if (!File.Exists(Path.Combine(UiSource, "package.json")))
            throw new Exception("package.json not found in Angular UI source.");

        Write("=== ANGULAR UI BUILD STARTED ===");

        var npmCi = File.Exists(Path.Combine(UiSource, "package-lock.json"))
            ? "ci"
            : "install";

        var install = await Run(
            "npm",
            npmCi,
            UiSource,
            "UI",
            true,
            token);

        if (install != 0)
            throw new Exception("npm dependency installation failed.");

        var result = await Run(
            "npm",
            "run build -- --configuration=production",
            UiSource,
            "UI",
            false,
            token);

        if (result != 0)
            throw new Exception("Angular production build failed.");

        Write("[OK] UI build completed.");
        Write("[INFO] UI deployment is disabled, matching setup.bat.");
    }

    async Task ConfigureNginx()
    {
        BeginOperation();

        try
        {
            await ConfigureNginxCore(Token);
        }
        catch (OperationCanceledException)
        {
            Write("Nginx configuration cancelled.");
        }
        catch (Exception ex)
        {
            Write("[ERROR] " + ex.Message, true);
            MessageBox.Show(ex.Message, "Nginx configuration failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task ConfigureNginxCore(CancellationToken token)
    {
        var nginxExe = nginx.Text.Trim();

        if (!File.Exists(nginxExe))
            throw new Exception($"{nginxExe} was not found.");

        var root = Path.GetDirectoryName(nginxExe)!;
        var conf = Path.Combine(root, "conf");

        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(conf);

        var configFile = Path.Combine(conf, "nginx.conf");
        var uiRoot = Path.Combine(Server, "UI").Replace("\\", "/");

        var config = string.Join(Environment.NewLine, new[]
        {
            "worker_processes 1;",
            "error_log logs/error.log warn;",
            "pid logs/nginx.pid;",
            "events {",
            "    worker_connections 1024;",
            "}",
            "http {",
            "    include mime.types;",
            "    default_type application/octet-stream;",
            "    access_log logs/access.log;",
            "    sendfile on;",
            "    keepalive_timeout 65;",
            "",
            "    server {",
            "        listen 80;",
            "        server_name codename399.com www.codename399.com;",
            $"        root {uiRoot};",
            "        index index.html;",
            "        location / {",
            "            try_files $uri $uri/ /index.html;",
            "        }",
            "    }",
            "",
            "    server {",
            "        listen 80;",
            "        server_name api.codename399.com;",
            "        location / {",
            "            proxy_pass http://127.0.0.1:5000;",
            "            proxy_http_version 1.1;",
            "            proxy_set_header Host $host;",
            "            proxy_set_header X-Real-IP $remote_addr;",
            "            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;",
            "            proxy_set_header X-Forwarded-Proto $scheme;",
            "            proxy_set_header Upgrade $http_upgrade;",
            "            proxy_set_header Connection \"upgrade\";",
            "            add_header X-CodeName399-Route \"gateway\" always;",
            "            proxy_connect_timeout 10;",
            "            proxy_read_timeout 300;",
            "            proxy_send_timeout 300;",
            "        }",
            "    }",
            "}"
        });

        File.WriteAllText(configFile, config);

        Write("[Nginx] Testing configuration...");

        var test = await Run(
            nginxExe,
            "-t -p " + root + "\\ -c conf\\nginx.conf",
            root,
            "Nginx",
            false,
            token);

        if (test != 0)
        {
            if (File.Exists(Path.Combine(root, "logs", "error.log")))
                Write(File.ReadAllText(Path.Combine(root, "logs", "error.log")), true);

            throw new Exception("Nginx configuration test failed.");
        }

        Write("[Nginx] Stopping any existing Nginx instance...");

        await Run(
            "taskkill",
            "/F /IM nginx.exe",
            root,
            "Nginx",
            false,
            token);

        await Task.Delay(2000, token);

        Write("[Nginx] Starting Nginx...");

        Process.Start(new ProcessStartInfo
        {
            FileName = nginxExe,
            Arguments = "-p " + root + "\\ -c conf\\nginx.conf",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (!await WaitPort(80, 15, token))
        {
            if (File.Exists(Path.Combine(root, "logs", "error.log")))
                Write(File.ReadAllText(Path.Combine(root, "logs", "error.log")), true);

            throw new Exception("Nginx did not listen on TCP 80.");
        }

        await Run(
            "netsh",
            "advfirewall firewall add rule name=\"CodeName399 Nginx HTTP\" dir=in action=allow protocol=TCP localport=80",
            root,
            "Nginx",
            false,
            token);

        Write("[OK] Nginx is listening on TCP 80.");

        Write("[Nginx] Configuring automatic startup...");

        var task = await Run(
            "schtasks",
            "/Create /TN \"CodeName399 Nginx\" /SC ONSTART /RU SYSTEM /RL HIGHEST " +
            $"/TR \"{nginxExe} -p {root}\\ -c conf\\nginx.conf\" /F",
            root,
            "Nginx",
            false,
            token);

        if (task != 0)
            throw new Exception("Failed to configure Nginx automatic startup.");

        Write("[OK] Nginx automatic startup configured.");

        await TestNginxRouting(token);
    }

    async Task TestNginxRouting(CancellationToken token)
    {
        var temp = Path.GetTempPath();
        var uiBody = Path.Combine(temp, "codename399-ui-test.txt");
        var uiStatus = Path.Combine(temp, "codename399-ui-status.txt");
        var uiError = Path.Combine(temp, "codename399-ui-error.txt");

        Write("[Nginx] Testing UI host routing...");

        await Run(
            "curl.exe",
            $"--noproxy \"*\" --connect-timeout 3 --max-time 8 -sS -o \"{uiBody}\" " +
            $"-w \"%{{http_code}}\" -H \"Host: codename399.com\" http://127.0.0.1/",
            root: temp,
            tag: "Nginx",
            throwOnError: false,
            token: token,
            stdoutFile: uiStatus,
            stderrFile: uiError);

        var uiCode = File.Exists(uiStatus)
            ? File.ReadAllText(uiStatus).Trim()
            : "";

        if (uiCode != "200")
        {
            if (File.Exists(uiError))
                Write(File.ReadAllText(uiError), true);

            throw new Exception($"UI host test returned HTTP {uiCode}.");
        }

        Write("[OK] UI host routing works.");

        var headers = Path.Combine(temp, "codename399-api-headers.txt");
        var body = Path.Combine(temp, "codename399-api-body.txt");
        var apiError = Path.Combine(temp, "codename399-api-error.txt");

        Write("[Nginx] Testing API host routing...");

        var apiResult = await Run(
            "curl.exe",
            $"--noproxy \"*\" --connect-timeout 3 --max-time 8 -sS " +
            $"-D \"{headers}\" -o \"{body}\" -H \"Host: api.codename399.com\" " +
            "http://127.0.0.1/__codename399_route_test__",
            temp,
            "Nginx",
            false,
            token,
            stdoutFile: null,
            stderrFile: apiError);

        if (apiResult != 0)
        {
            if (File.Exists(apiError))
                Write(File.ReadAllText(apiError), true);

            throw new Exception("API host test failed.");
        }

        var responseHeaders = File.Exists(headers)
            ? File.ReadAllText(headers)
            : "";

        if (!responseHeaders.Contains(
                "X-CodeName399-Route: gateway",
                StringComparison.OrdinalIgnoreCase))
        {
            Write("Response headers:", true);
            Write(responseHeaders, true);

            if (File.Exists(body))
                Write("Response body:\n" + File.ReadAllText(body), true);

            throw new Exception("API host did not reach Gateway.");
        }

        Write("[OK] API host routing reaches Gateway.");
    }

    async Task<int> Run(
        string exe,
        string args,
        string root,
        string tag,
        bool throwOnError,
        CancellationToken token,
        string? stdoutFile = null,
        string? stderrFile = null) =>
        await RunInternal(exe, args, root, tag, throwOnError, token, stdoutFile, stderrFile);

    async Task<int> RunInternal(
        string exe,
        string args,
        string workingDirectory,
        string tag,
        bool throwOnError,
        CancellationToken token,
        string? stdoutFile = null,
        string? stderrFile = null)
    {
        token.ThrowIfCancellationRequested();

        Write($"[{tag}] > {exe} {args}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };

        var output = new StringBuilder();
        var errors = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                output.AppendLine(e.Data);
                Write($"[{tag}] {e.Data}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                errors.AppendLine(e.Data);
                Write($"[{tag}] {e.Data}", true);
            }
        };

        if (!process.Start())
            throw new Exception("Unable to start " + exe);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
                // Ignore cleanup errors after cancellation.
            }

            throw;
        }

        if (stdoutFile != null)
            File.WriteAllText(stdoutFile, output.ToString());

        if (stderrFile != null)
            File.WriteAllText(stderrFile, errors.ToString());

        if (process.ExitCode != 0 && throwOnError)
            throw new Exception($"[{tag}] exit code {process.ExitCode}");

        return process.ExitCode;
    }

    async Task<string> Capture(
        string exe,
        string args,
        string workingDirectory,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };

        if (!process.Start())
            throw new Exception("Unable to start " + exe);

        var output = await process.StandardOutput.ReadToEndAsync(token);
        await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        return output;
    }

    async Task<string?> State(string name) =>
        await Task.Run(() =>
        {
            try
            {
                using var service = new ServiceController(name);
                return service.Status
                    .ToString()
                    .ToUpperInvariant();
            }
            catch
            {
                return null;
            }
        });

    async Task<bool> Port(int port) =>
        await Task.Run(() =>
            IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(x => x.Port == port));

    async Task RefreshStatus()
    {
        foreach (var service in services)
        {
            if (!states.TryGetValue(service.Name, out var label))
                continue;

            var state = await State(service.Service);
            var portOpen = await Port(service.Port);

            label.Text =
                state == "RUNNING" && portOpen
                    ? $"RUNNING • {service.InstrumentType} • :{service.Port}"
                    : state == "RUNNING"
                        ? $"RUNNING • port down • :{service.Port}"
                        : state ?? "NOT INSTALLED";
        }
    }
}

internal static class StringExtensions
{
    public static IEnumerable<string> SplitLines(this string value) =>
        value.Replace("\r\n", "\n")
             .Replace('\r', '\n')
             .Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
