using System.Diagnostics;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text;

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
    TextBox source = new(), server = new(), nginx = new(), uiSource = new();
    RichTextBox log = new();

    // NEW ARCHITECTURE:
    // Auth + Gateway + three independent trading runtimes.
    // Each trading runtime owns instrument loading, quotes, evaluation,
    // virtual/paper/live trading, optimization and stock performance.
    readonly List<ServiceDef> services = new()
    {
        new("Gateway",
            @"CodeName399.Gateway\CodeName399.Gateway.csproj",
            "Gateway", "CodeName399.Gateway", 5000,
            "CodeName399.Gateway.exe", "Shared"),

        new("Auth",
            @"CodeName399.Auth.API\CodeName399.Auth.API.csproj",
            "Auth", "CodeName399.Auth", 5001,
            "CodeName399.Auth.API.exe", "Shared"),

        new("EquityTrading",
            @"CodeName399.EquityTrading.API\CodeName399.EquityTrading.API.csproj",
            "EquityTrading", "CodeName399.EquityTrading", 5109,
            "CodeName399.EquityTrading.API.exe", "Equity"),

        new("FutureTrading",
            @"CodeName399.FutureTrading.API\CodeName399.FutureTrading.API.csproj",
            "FutureTrading", "CodeName399.FutureTrading", 5110,
            "CodeName399.FutureTrading.API.exe", "Futures"),

        new("OptionsTrading",
            @"CodeName399.OptionsTrading.API\CodeName399.OptionsTrading.API.csproj",
            "OptionsTrading", "CodeName399.OptionsTrading", 5111,
            "CodeName399.OptionsTrading.API.exe", "Options")
    };

    // Services removed by the consolidation.
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

    Button fullSetup = null!, build = null!, publish = null!, restart = null!,
           stop = null!, cancel = null!, nginxBtn = null!, uiDeploy = null!;
    CancellationTokenSource? cts;

    public MainForm()
    {
        Text = "CodeName399 Server Setup Manager";
        Width = 1400;
        Height = 900;
        MinimumSize = new Size(1150, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 165,
            ColumnCount = 3,
            RowCount = 4,
            Padding = new Padding(10)
        };

        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        AddPath(top, 0, "Source", source,
            Environment.GetEnvironmentVariable("CODENAME399_SOURCE")
            ?? @"C:\Users\gauda\source\repos\codename399\CodeName399.API");

        AddPath(top, 1, "Server Root", server, @"C:\Servers\CodeName399");
        AddPath(top, 2, "Nginx EXE", nginx, @"C:\nginx\nginx.exe");

        var browse1 = Btn("Browse", (_, _) => BrowseFolder(source));
        var browse2 = Btn("Browse", (_, _) => BrowseFolder(server));
        var browse3 = Btn("Browse", (_, _) => BrowseFile(nginx));

        top.Controls.Add(browse1, 2, 0);
        top.Controls.Add(browse2, 2, 1);
        top.Controls.Add(browse3, 2, 2);

        AddPath(top, 3, "Angular UI", uiSource,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "source", "repos", "codename399", "home"));

        top.Controls.Add(Btn("Browse", (_, _) => BrowseFolder(uiSource)), 2, 3);

        var main = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Orientation = Orientation.Vertical
        };

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };

        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));

        var all = new CheckBox
        {
            Text = "Select All",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(6)
        };

        all.CheckedChanged += (_, _) =>
        {
            foreach (var cb in selected.Values)
                cb.Checked = all.Checked;
        };

        left.Controls.Add(all, 0, 0);

        var list = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 4,
            Padding = new Padding(8),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };

        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));

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
                Text = s.InstrumentType,
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

            list.Controls.Add(cb);
            list.Controls.Add(name);
            list.Controls.Add(type);
            list.Controls.Add(state);
        }

        var uiCb = new CheckBox { AutoSize = true, Margin = new Padding(4) };
        selected["Angular UI"] = uiCb;

        var uiLabel = new Label
        {
            Text = "Angular UI",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(4, 8, 4, 4),
            Font = new Font(Font, FontStyle.Bold)
        };

        var uiState = new Label
        {
            Text = "NOT DEPLOYED",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(4, 8, 4, 4)
        };

        states["Angular UI"] = uiState;

        list.Controls.Add(uiCb);
        list.Controls.Add(uiLabel);
        list.Controls.Add(new Label
        {
            Text = "Web",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(4, 8, 4, 4)
        });
        list.Controls.Add(uiState);

        left.Controls.Add(list, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            AutoScroll = true
        };

        fullSetup = Btn("FULL SETUP", async (_, _) => await FullSetup());
        build = Btn("Build Solution", async (_, _) => await BuildSolution());
        publish = Btn("Publish Selected", async (_, _) => await PublishSelected());
        uiDeploy = Btn("Build & Deploy Angular UI", async (_, _) => await BuildDeployUi());
        restart = Btn("Restart All", async (_, _) => await RestartAll());
        stop = Btn("Stop Selected", async (_, _) => await StopSelected());
        cancel = Btn("Cancel", (_, _) => CancelCurrentOperation());
        nginxBtn = Btn("Configure / Restart Nginx", async (_, _) => await ConfigureNginx());

        actions.Controls.Add(fullSetup);
        actions.Controls.Add(build);
        actions.Controls.Add(publish);
        actions.Controls.Add(uiDeploy);
        actions.Controls.Add(restart);
        actions.Controls.Add(stop);
        actions.Controls.Add(cancel);
        actions.Controls.Add(nginxBtn);
        actions.Controls.Add(Btn("Refresh Status", async (_, _) => await RefreshStatus()));
        actions.Controls.Add(Btn("Clear Log", (_, _) => log.Clear()));

        left.Controls.Add(actions, 0, 2);
        main.Panel1.Controls.Add(left);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };

        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        right.Controls.Add(new Label
        {
            Text = "Live Setup / Deployment Log",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(5, 10, 0, 0)
        }, 0, 0);

        log.Dock = DockStyle.Fill;
        log.ReadOnly = true;
        log.Font = new Font("Consolas", 9);
        log.BackColor = Color.FromArgb(20, 22, 26);
        log.ForeColor = Color.Gainsboro;

        right.Controls.Add(log, 0, 1);
        main.Panel2.Controls.Add(right);

        Controls.Add(main);
        Controls.Add(top);

        Shown += async (_, _) =>
        {
            SetSafeSplitterDistance(main);
            await RefreshStatus();
        };

        Resize += (_, _) => SetSafeSplitterDistance(main);
    }

    void SetSafeSplitterDistance(SplitContainer split)
    {
        if (split.ClientSize.Width <= 0)
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

    string Source => source.Text.Trim();
    string Server => server.Text.Trim();
    string UiSource => uiSource.Text.Trim();
    string Sln => Path.Combine(Source, "CodeName399.sln");

    void AddPath(TableLayoutPanel t, int row, string label, TextBox box, string value)
    {
        box.Text = value;
        box.Dock = DockStyle.Fill;
        t.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        }, 0, row);
        t.Controls.Add(box, 1, row);
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

    void BrowseFolder(TextBox t)
    {
        using var d = new FolderBrowserDialog { SelectedPath = t.Text };
        if (d.ShowDialog() == DialogResult.OK)
            t.Text = d.SelectedPath;
    }

    void BrowseFile(TextBox t)
    {
        using var d = new OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe"
        };

        if (d.ShowDialog() == DialogResult.OK)
            t.Text = d.FileName;
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

    async Task BuildSolution()
    {
        BeginOperation();

        try
        {
            ValidateSource();
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

    void ValidateSource()
    {
        if (!Directory.Exists(Source))
            throw new Exception("Source directory not found: " + Source);

        if (!File.Exists(Sln))
            throw new Exception("Solution not found: " + Sln);

        foreach (var service in services)
        {
            var project = Path.Combine(Source, service.Project);
            if (!File.Exists(project))
                throw new Exception(
                    $"[{service.Name}] project not found: {project}");
        }
    }

    async Task RestoreAndBuild(CancellationToken token)
    {
        Write("=== RESTORE STARTED ===");
        var restore = await Run("dotnet",
            $"restore \"{Sln}\"",
            Source,
            "Restore",
            true,
            token);

        if (restore != 0)
            throw new Exception("dotnet restore failed.");

        Write("=== BUILD STARTED ===");

        var build = await Run("dotnet",
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

            ValidateSource();

            Directory.CreateDirectory(Server);

            foreach (var service in services)
                Directory.CreateDirectory(Path.Combine(Server, service.Folder));

            Write("[1/9] Consolidated service directories ready.");

            await RemoveLegacyServices(Token);
            Write("[2/9] Legacy microservices removed.");

            await BackupConfig();
            Write("[3/9] Existing configuration backed up.");

            await RestoreAndBuild(Token);
            Write("[4/9] Solution restored and compiled successfully.");

            foreach (var service in services)
            {
                Token.ThrowIfCancellationRequested();
                await Deploy(service, Token);
            }

            Write("[5/9] Five consolidated services published.");

            await ValidateGateway(Token);
            Write("[6/9] Gateway startup validation passed.");

            // Critical: all five services are started concurrently.
            await RestartAllCore(Token);
            Write("[7/9] Auth, Gateway, Equity, Futures and Options started together.");

            if (selected.TryGetValue("Angular UI", out var ui) && ui.Checked)
                await BuildDeployUiCore(Token);

            await ConfigureNginxCore(Token);
            Write("[8/9] Nginx configured and verified.");

            await RefreshStatus();
            Write("[9/9] Final service/port verification completed.");
            Write("=== FULL SERVER SETUP SUCCESSFUL ===");
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

    async Task RemoveLegacyServices(CancellationToken token)
    {
        foreach (var service in legacyServices)
        {
            token.ThrowIfCancellationRequested();

            var query = await Run(
                "sc.exe",
                $"query \"{service}\"",
                Source,
                service,
                false,
                token);

            if (query == 0)
            {
                Write($"[{service}] Removing legacy Windows service...");
                await Run("sc.exe",
                    $"stop \"{service}\"",
                    Source,
                    service,
                    false,
                    token);

                await WaitForServiceStopped(service, token);

                await Run("sc.exe",
                    $"delete \"{service}\"",
                    Source,
                    service,
                    false,
                    token);
            }
        }
    }

    async Task BackupConfig()
    {
        var backup = Path.Combine(
            Server,
            "_backups",
            $"BeforeSetup_{DateTime.Now:yyyyMMdd_HHmmss}");

        Directory.CreateDirectory(backup);

        foreach (var service in services)
        {
            var file = Path.Combine(
                Server,
                service.Folder,
                "appsettings.json");

            if (File.Exists(file))
                File.Copy(
                    file,
                    Path.Combine(backup, service.Name + "-appsettings.json"),
                    true);
        }

        var ocelot = Path.Combine(Server, "Gateway", "ocelot.json");

        if (File.Exists(ocelot))
            File.Copy(
                ocelot,
                Path.Combine(backup, "Gateway-ocelot.json"),
                true);

        await Task.CompletedTask;
    }

    async Task Deploy(ServiceDef service, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        Write($"[{service.Name}] Stopping service...");
        await Stop(service, token);

        var project = Path.Combine(Source, service.Project);
        var destination = Path.Combine(Server, service.Folder);

        Directory.CreateDirectory(destination);

        Write($"[{service.Name}] Publishing Release build...");

        var result = await Run(
            "dotnet",
            $"publish \"{project}\" -c Release -o \"{destination}\" --no-restore -p:ErrorOnDuplicatePublishOutputFiles=false",
            Source,
            service.Name,
            true,
            token);

        if (result != 0)
            throw new Exception($"[{service.Name}] publish failed.");

        await EnsureService(service, token);
    }

    async Task PublishSelected()
    {
        var selectedServices = services
            .Where(x => selected[x.Name].Checked)
            .ToList();

        if (selectedServices.Count == 0)
        {
            MessageBox.Show("Select at least one microservice.");
            return;
        }

        BeginOperation();

        try
        {
            foreach (var service in selectedServices)
                await Deploy(service, Token);
        }
        catch (OperationCanceledException)
        {
            Write("Publish cancelled.");
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
            throw new Exception(
                $"[{service.Name}] executable not found: {exe}");

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
            await Run(
                "sc.exe",
                $"create \"{service.Service}\" binPath= \"{binPath}\" start= auto DisplayName= \"{service.Service}\" obj= LocalSystem",
                Source,
                service.Name,
                true,
                token);
        }
        else
        {
            await Run(
                "sc.exe",
                $"config \"{service.Service}\" start= auto binPath= \"{binPath}\" obj= LocalSystem",
                Source,
                service.Name,
                true,
                token);
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
            await RestartAllCore(Token);
        }
        catch (OperationCanceledException)
        {
            Write("Restart cancelled.");
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task RestartAllCore(CancellationToken token)
    {
        Write("=== STOPPING ALL CONSOLIDATED SERVICES ===");

        // Stop in parallel so a slow service cannot block the others.
        await Task.WhenAll(
            services.Select(s => Stop(s, token)));

        token.ThrowIfCancellationRequested();

        Write("=== STARTING ALL CONSOLIDATED SERVICES SIMULTANEOUSLY ===");

        // This is intentional: Equity, Futures and Options must run at
        // the same time, alongside Auth and Gateway.
        await Task.WhenAll(
            services.Select(s => Start(s, token)));

        Write("=== ALL CONSOLIDATED SERVICES ARE RUNNING ===");
    }

    async Task StopSelected()
    {
        var selectedServices = services
            .Where(x => selected[x.Name].Checked)
            .ToList();

        if (selectedServices.Count == 0)
        {
            MessageBox.Show("Select at least one microservice.");
            return;
        }

        BeginOperation();

        try
        {
            await Task.WhenAll(
                selectedServices.Select(s => Stop(s, Token)));
        }
        catch (OperationCanceledException)
        {
            Write("Stop cancelled.");
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
            return;

        await Run(
            "sc.exe",
            $"stop \"{service.Service}\"",
            Source,
            service.Name,
            false,
            token);

        await WaitForServiceStopped(service.Service, token);
    }

    async Task WaitForServiceStopped(string serviceName, CancellationToken token)
    {
        for (var i = 0; i < 45; i++)
        {
            token.ThrowIfCancellationRequested();

            var state = await State(serviceName);

            if (state is null || state == "STOPPED")
                return;

            await Task.Delay(1000, token);
        }
    }

    async Task Start(ServiceDef service, CancellationToken token)
    {
        await Run(
            "sc.exe",
            $"start \"{service.Service}\"",
            Source,
            service.Name,
            false,
            token);

        for (var i = 0; i < 45; i++)
        {
            token.ThrowIfCancellationRequested();

            var state = await State(service.Service);

            if (state == "RUNNING" && await Port(service.Port))
            {
                Write(
                    $"[{service.Name}] RUNNING • {service.InstrumentType} • TCP {service.Port}");
                return;
            }

            await Task.Delay(1000, token);
        }

        throw new Exception(
            $"{service.Name} did not start or TCP {service.Port} is not listening.");
    }

    async Task ValidateGateway(CancellationToken token)
    {
        var gateway = services.First(x => x.Name == "Gateway");
        var exe = Path.Combine(Server, gateway.Folder, gateway.Exe);

        if (!File.Exists(exe))
            throw new Exception("Gateway executable missing.");

        Write("[Gateway] Validating startup...");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--environment Production --urls http://127.0.0.1:5000",
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });

        if (process == null)
            throw new Exception("Could not start Gateway validation.");

        await Task.Delay(3000, token);

        if (process.HasExited)
            throw new Exception(
                "Gateway validation process exited with code " +
                process.ExitCode);

        process.Kill(true);
    }

    async Task BuildDeployUi()
    {
        BeginOperation();

        try
        {
            await BuildDeployUiCore(Token);
        }
        catch (OperationCanceledException)
        {
            Write("Angular UI deployment cancelled.");
        }
        catch (Exception ex)
        {
            Write("[ERROR] " + ex.Message, true);
            MessageBox.Show(ex.Message, "UI deployment failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
            await RefreshStatus();
        }
    }

    async Task BuildDeployUiCore(CancellationToken token)
    {
        if (!Directory.Exists(UiSource))
            throw new Exception(
                "Angular UI source directory not found: " + UiSource);

        if (!File.Exists(Path.Combine(UiSource, "package.json")))
            throw new Exception(
                "package.json not found in Angular UI source.");

        var uiDestination = Path.Combine(Server, "UI");
        Directory.CreateDirectory(uiDestination);

        Write("=== ANGULAR UI BUILD & DEPLOY STARTED ===");

        await Run("npm", "ci", UiSource, "UI", true, token);

        var result = await Run(
            "npm",
            "run build -- --configuration production",
            UiSource,
            "UI",
            false,
            token);

        if (result != 0)
        {
            result = await Run(
                "ng",
                "build --configuration production",
                UiSource,
                "UI",
                false,
                token);
        }

        if (result != 0)
            throw new Exception("Angular production build failed.");

        var dist = FindAngularDist(UiSource);

        if (dist == null)
            throw new Exception(
                "Angular build completed but dist output was not found.");

        await Run(
            "robocopy",
            $"\"{dist}\" \"{uiDestination}\" /E /R:2 /W:1",
            UiSource,
            "UI",
            false,
            token);

        Write("[UI] Files deployed to " + uiDestination);
        Write("=== ANGULAR UI DEPLOYMENT SUCCESSFUL ===");
    }

    string? FindAngularDist(string root)
    {
        var dist = Path.Combine(root, "dist");

        if (!Directory.Exists(dist))
            return null;

        var index = Directory.GetFiles(
                dist,
                "index.html",
                SearchOption.AllDirectories)
            .FirstOrDefault();

        return index == null
            ? null
            : Path.GetDirectoryName(index);
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
        {
            Write("[WARN] Nginx executable not found; skipped.");
            return;
        }

        var root = Path.GetDirectoryName(nginxExe)!;
        var conf = Path.Combine(root, "conf");

        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(conf);

        var configFile = Path.Combine(conf, "nginx.conf");
        var uiRoot = Path.Combine(Server, "UI").Replace("\\", "/");

        // Do NOT use an interpolated raw string here.
        // Nginx uses '$...' variables and '{...}' blocks, which C# would
        // otherwise interpret as interpolation syntax.
        var config = string.Join(Environment.NewLine, new[]
        {
            "worker_processes 1;",
            "error_log logs/error.log warn;",
            "pid logs/nginx.pid;",
            "",
            "events {",
            "    worker_connections 1024;",
            "}",
            "",
            "http {",
            "    include mime.types;",
            "    default_type application/octet-stream;",
            "    sendfile on;",
            "    keepalive_timeout 65;",
            "",
            "    server {",
            "        listen 80;",
            "        server_name codename399.com www.codename399.com;",
            "",
            $"        root {uiRoot};",
            "        index index.html;",
            "",
            "        location / {",
            "            try_files $uri $uri/ /index.html;",
            "        }",
            "    }",
            "",
            "    server {",
            "        listen 80;",
            "        server_name api.codename399.com;",
            "",
            "        location / {",
            "            proxy_pass http://127.0.0.1:5000;",
            "            proxy_http_version 1.1;",
            "            proxy_set_header Host $host;",
            "            proxy_set_header X-Real-IP $remote_addr;",
            "            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;",
            "            proxy_set_header X-Forwarded-Proto $scheme;",
            "            proxy_set_header Upgrade $http_upgrade;",
            "            proxy_set_header Connection \"upgrade\";",
            "            proxy_connect_timeout 10;",
            "            proxy_read_timeout 300;",
            "            proxy_send_timeout 300;",
            "        }",
            "    }",
            "}"
        });

        File.WriteAllText(configFile, config);

        var test = await Run(
            nginxExe,
            $"-t -p \"{root}\" -c conf\\nginx.conf",
            root,
            "Nginx",
            false,
            token);

        if (test != 0)
            throw new Exception("Nginx configuration test failed.");

        Write("[Nginx] Stopping nginx...");

        await Run(
            "taskkill",
            "/F /IM nginx.exe",
            root,
            "Nginx",
            false,
            token);

        await Task.Delay(1000, token);

        Write("[Nginx] Starting nginx...");

        Process.Start(new ProcessStartInfo
        {
            FileName = nginxExe,
            Arguments = $"-p \"{root}\" -c conf\\nginx.conf",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        for (var i = 0; i < 15; i++)
        {
            token.ThrowIfCancellationRequested();

            if (await Port(80))
                break;

            await Task.Delay(1000, token);
        }

        if (!await Port(80))
            throw new Exception(
                "Nginx did not start/listen on TCP 80.");

        await Run(
            "netsh",
            "advfirewall firewall add rule name=\"CodeName399 Nginx HTTP\" dir=in action=allow protocol=TCP localport=80",
            root,
            "Nginx",
            false,
            token);

        Write("[Nginx] Restarted and TCP 80 verified.");
    }

    async Task<int> Run(
        string exe,
        string args,
        string tag,
        bool throwOnError = true,
        CancellationToken token = default) =>
        await Run(exe, args, Source, tag, throwOnError, token);

    async Task<int> Run(
        string exe,
        string args,
        string workingDirectory,
        string tag,
        bool throwOnError = true,
        CancellationToken token = default)
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

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Write($"[{tag}] {e.Data}");
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Write($"[{tag}] {e.Data}", true);
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

        if (process.ExitCode != 0 && throwOnError)
            throw new Exception(
                $"[{tag}] exit code {process.ExitCode}");

        return process.ExitCode;
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
            var state = await State(service.Service);
            var portOpen = await Port(service.Port);

            states[service.Name].Text =
                state == "RUNNING" && portOpen
                    ? $"RUNNING • {service.InstrumentType} • :{service.Port}"
                    : state == "RUNNING"
                        ? $"RUNNING • port down • :{service.Port}"
                        : state ?? "NOT INSTALLED";
        }

        if (states.ContainsKey("Angular UI"))
        {
            states["Angular UI"].Text =
                await Port(80)
                    ? "NGINX RUNNING • UI :80"
                    : "NGINX STOPPED";
        }
    }
}
