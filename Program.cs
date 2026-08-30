using System.Diagnostics;
using System.Text;
using System.ServiceProcess;

namespace CodeName399.DeploymentManager;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed record ServiceInfo(string Name, string ProjectRelativePath, string PublishProfile, string ServerFolder, string WindowsService, int Port, string Executable);

public sealed class AppConfig
{
    public string BasePath { get; set; } = @"C:\Users\gauda\source\repos\codename399";
    public string ApiPath => Path.Combine(BasePath, "CodeName399.API");
    public string AngularPath => Path.Combine(BasePath, "CodeName399.UI");
    public string ServerRoot { get; set; } = @"C:\Servers\CodeName399";
    public string NginxExe { get; set; } = @"C:\nginx\nginx.exe";
}

public sealed class MainForm : Form
{
    readonly AppConfig config = new();
    readonly List<ServiceInfo> services = new()
    {
        new("Auth",
            @"CodeName399.Auth.API\CodeName399.Auth.API.csproj",
            "FolderProfile",
            "Auth",
            "CodeName399.Auth",
            5001,
            "CodeName399.Auth.API.exe"),

        new("Gateway",
            @"CodeName399.Gateway\CodeName399.Gateway.csproj",
            "FolderProfile",
            "Gateway",
            "CodeName399.Gateway",
            5000,
            "CodeName399.Gateway.exe"),

        new("EquityTrading",
            @"CodeName399.EquityTrading.API\CodeName399.EquityTrading.API.csproj",
            "FolderProfile",
            "EquityTrading",
            "CodeName399.EquityTrading",
            5109,
            "CodeName399.EquityTrading.API.exe"),

        new("FutureTrading",
            @"CodeName399.FutureTrading.API\CodeName399.FutureTrading.API.csproj",
            "FolderProfile",
            "FutureTrading",
            "CodeName399.FutureTrading",
            5110,
            "CodeName399.FutureTrading.API.exe"),

        new("OptionsTrading",
            @"CodeName399.OptionsTrading.API\CodeName399.OptionsTrading.API.csproj",
            "FolderProfile",
            "OptionsTrading",
            "CodeName399.OptionsTrading",
            5111,
            "CodeName399.OptionsTrading.API.exe")
    };

    readonly Dictionary<string, CheckBox> checks = new();
    readonly Dictionary<string, Label> stateLabels = new();
    readonly Dictionary<string, Button> localRunButtons = new();
    readonly Dictionary<string, Button> localStopButtons = new();
    readonly Dictionary<string, Process> localProcesses = new();
    Label uiStateLabel = null!;
    readonly RichTextBox log = new();
    readonly Button[] actionButtons;
    readonly Button cancelButton;
    CheckBox uiCheck = null!;
    CancellationTokenSource? operationCts;

    public MainForm()
    {
        Text = "CodeName399 Deployment Manager";
        Width = 1250; Height = 820; MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 86, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(new Label { Text = "Source", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        var source = new TextBox { Text = config.BasePath, Dock = DockStyle.Fill };
        top.Controls.Add(source, 1, 0);
        top.Controls.Add(new Label { Text = "Server", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var server = new TextBox { Text = config.ServerRoot, Dock = DockStyle.Fill };
        top.Controls.Add(server, 1, 1);
        source.TextChanged += (_, _) => config.BasePath = source.Text.Trim();
        server.TextChanged += (_, _) => config.ServerRoot = server.Text.Trim();

        var main = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 520, Padding = new Padding(10) };
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        var selectAll = new CheckBox { Text = "Select All Services", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(6) };
        selectAll.CheckedChanged += (_, _) => { foreach (var c in checks.Values) c.Checked = selectAll.Checked; uiCheck.Checked = selectAll.Checked; };
        left.Controls.Add(selectAll, 0, 0);

        var list = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 4, Padding = new Padding(4), GrowStyle = TableLayoutPanelGrowStyle.AddRows };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        foreach (var s in services)
        {
            var cb = new CheckBox { AutoSize = true, Text = s.Name, Tag = s.Name, Margin = new Padding(4, 7, 4, 4), Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) };
            checks[s.Name] = cb;
            var state = new Label { Text = "Checking...", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 8, 4, 4) };
            stateLabels[s.Name] = state;
            var runLocal = MakeButton("Run", async (_, _) => await RunLocalAsync(s));
            runLocal.Width = 68; runLocal.Enabled = true;
            var stopLocal = MakeButton("Stop", (_, _) => StopLocal(s));
            stopLocal.Width = 68; stopLocal.Enabled = false;
            localRunButtons[s.Name] = runLocal; localStopButtons[s.Name] = stopLocal;
            list.Controls.Add(cb); list.Controls.Add(state); list.Controls.Add(runLocal); list.Controls.Add(stopLocal);
        }
        uiCheck = new CheckBox { AutoSize = true, Text = "UI (Angular)", Tag = "UI", Margin = new Padding(4, 7, 4, 4), Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) };
        var uiState = new Label { Text = "Nginx / static files", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 8, 4, 4) };
        uiStateLabel = uiState; list.Controls.Add(uiCheck); list.Controls.Add(uiState); list.Controls.Add(new Label { Text = "", AutoSize = true }); list.Controls.Add(new Label { Text = "", AutoSize = true });
        left.Controls.Add(list, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true, Padding = new Padding(0, 4, 0, 0) };
        actionButtons = new[] { MakeButton("Rebuild Selected", (_, _) => RunSelected("REBUILD")), MakeButton("Rebuild Solution", (_, _) => RebuildSolution()), MakeButton("Build & Deploy UI", (_, _) => BuildDeployUi()), MakeButton("Clean", (_, _) => RunSelected("CLEAN")), MakeButton("Cleanup", (_, _) => RunSelected("CLEANUP")), MakeButton("Redeploy", (_, _) => RunSelected("REDEPLOY")), MakeButton("Stop Service", (_, _) => StopSelected()), MakeButton("Restart Service", (_, _) => RestartSelected()) };
        foreach (var b in actionButtons) buttons.Controls.Add(b);
        cancelButton = MakeButton("Cancel", (_, _) => CancelOperation()); cancelButton.Width = 92; cancelButton.Enabled = false; buttons.Controls.Add(cancelButton);
        left.Controls.Add(buttons, 0, 2);
        main.Panel1.Controls.Add(left);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logHeader = new FlowLayoutPanel { Dock = DockStyle.Fill };
        logHeader.Controls.Add(new Label { Text = "Live Process Log", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(4, 8, 20, 0) });
        var refresh = MakeButton("Refresh Status", async (_, _) => await RefreshStatusAsync()); refresh.Width = 115; logHeader.Controls.Add(refresh);
        var clear = MakeButton("Clear", (_, _) => log.Clear()); clear.Width = 65; logHeader.Controls.Add(clear);
        var copyLogs = MakeButton("Copy Logs", (_, _) =>
        {
            try
            {
                if (string.IsNullOrEmpty(log.Text))
                {
                    WriteLog("[Logs] Nothing to copy.");
                    return;
                }

                Clipboard.SetText(log.Text);
                WriteLog("[Logs] Logs copied to clipboard.");
            }
            catch (Exception ex)
            {
                WriteLog($"[Logs] Copy failed: {ex.Message}", true);
            }
        });
        copyLogs.Width = 85;
        logHeader.Controls.Add(copyLogs);
        var customCommand = new TextBox
        {
            Width = 260,
            Height = 30,
            PlaceholderText = "Custom command (e.g. dotnet --info)",
            Margin = new Padding(10, 3, 4, 3)
        };
        var runCustomCommand = MakeButton("Run Command", async (_, _) => await RunCustomCommandAsync(customCommand.Text));
        runCustomCommand.Width = 105;
        logHeader.Controls.Add(customCommand);
        logHeader.Controls.Add(runCustomCommand);
        right.Controls.Add(logHeader, 0, 0);
        log.Dock = DockStyle.Fill; log.BackColor = Color.FromArgb(20, 22, 26); log.ForeColor = Color.Gainsboro; log.Font = new Font("Consolas", 9F); log.ReadOnly = true; log.DetectUrls = false;
        right.Controls.Add(log, 0, 1); main.Panel2.Controls.Add(right);

        Controls.Add(main); Controls.Add(top);
        Shown += async (_, _) => await RefreshStatusAsync();
    }

    Button MakeButton(string text, EventHandler handler) { var b = new Button { Text = text, AutoSize = true, Height = 34, MinimumSize = new Size(92, 34), Margin = new Padding(4) }; b.Click += handler; return b; }

    List<ServiceInfo> Selected() => checks.Where(x => x.Value.Checked).Select(x => services.First(s => s.Name == x.Key)).ToList();
    void SetBusy(bool busy)
    {
        foreach (var b in actionButtons) b.Enabled = !busy;
        if (cancelButton != null) cancelButton.Enabled = busy;
    }

    void CancelOperation()
    {
        var cts = operationCts;
        if (cts == null || cts.IsCancellationRequested) return;
        WriteLog("=== Cancellation requested ===", true);
        cts.Cancel();
    }

    void WriteLog(string text, bool error = false) { if (InvokeRequired) { BeginInvoke(() => WriteLog(text, error)); return; } log.SelectionStart = log.TextLength; log.SelectionColor = error ? Color.OrangeRed : Color.LightGray; log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}"); log.SelectionColor = log.ForeColor; log.ScrollToCaret(); }

    async Task RebuildSolution()
    {
        if (operationCts != null) return;
        var solution = Path.Combine(config.ApiPath, "CodeName399.sln");
        if (!File.Exists(solution)) { MessageBox.Show($"Solution not found: {solution}", "Rebuild Solution", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        operationCts = new CancellationTokenSource(); SetBusy(true);
        try
        {
            WriteLog("=== REBUILD SOLUTION started ===");
            await RunProcess("dotnet", $"clean \"{solution}\" -c Release", config.ApiPath, "SOLUTION", true, operationCts.Token);
            await RunProcess("dotnet", $"build \"{solution}\" -c Release", config.ApiPath, "SOLUTION", true, operationCts.Token);
            WriteLog("=== REBUILD SOLUTION finished ===");
        }
        catch (OperationCanceledException) { WriteLog("=== REBUILD SOLUTION cancelled ===", true); }
        catch (Exception ex) { WriteLog(ex.Message, true); MessageBox.Show(ex.Message, "Rebuild Solution failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { operationCts.Dispose(); operationCts = null; SetBusy(false); await RefreshStatusAsync(); }
    }

    async Task RunSelected(string operation)
    {
        if (operationCts != null) return;
        var selected = Selected(); if (selected.Count == 0 && !uiCheck.Checked) { MessageBox.Show("Select at least one microservice or the UI.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        operationCts = new CancellationTokenSource(); SetBusy(true);
        try
        {
            WriteLog($"=== {operation} started ===");
            if (operation == "CLEAN" || operation == "CLEANUP" || operation == "REBUILD") foreach (var s in selected) await RunProjectOperation(s, operation);
            if (operation == "REBUILD") foreach (var s in selected) await RunProjectOperation(s, "BUILD");
            if (operation == "REDEPLOY") foreach (var s in selected) await Redeploy(s);
            if (uiCheck.Checked && (operation == "REBUILD" || operation == "REDEPLOY")) await DeployUi();
            WriteLog($"=== {operation} finished ===");
        }
        catch (OperationCanceledException) { WriteLog($"=== {operation} cancelled ===", true); }
        catch (Exception ex) { WriteLog(ex.Message, true); MessageBox.Show(ex.Message, "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { operationCts.Dispose(); operationCts = null; SetBusy(false); await RefreshStatusAsync(); }
    }

    async Task RunProjectOperation(ServiceInfo s, string operation)
    {
        var project = Path.Combine(config.ApiPath, s.ProjectRelativePath);
        if (!File.Exists(project)) throw new FileNotFoundException($"Project not found: {project}");
        string args = operation switch { "CLEAN" => $"clean \"{project}\" -c Release", "BUILD" => $"build \"{project}\" -c Release", _ => $"clean \"{project}\" -c Release" };
        if (operation == "CLEANUP")
        {
            var dir = Path.GetDirectoryName(project)!; var bin = Path.Combine(dir, "bin"); var obj = Path.Combine(dir, "obj");
            WriteLog($"[{s.Name}] Removing bin/obj and publish staging"); await Task.Run(() => { operationCts!.Token.ThrowIfCancellationRequested(); SafeDelete(bin); operationCts.Token.ThrowIfCancellationRequested(); SafeDelete(obj); operationCts.Token.ThrowIfCancellationRequested(); SafeDelete(Path.Combine(dir, "bin", "Release", "net8.0", "publish", s.Name)); }, operationCts.Token); return;
        }
        await RunProcess("dotnet", args, config.ApiPath, s.Name, true, operationCts!.Token);
    }

    async Task Redeploy(ServiceInfo s)
    {
        await StopService(s);
        var project = Path.Combine(config.ApiPath, s.ProjectRelativePath); var projectDir = Path.GetDirectoryName(project)!;
        var stage = Path.Combine(projectDir, "bin", "Release", "net8.0", "publish", s.Name); var dest = Path.Combine(config.ServerRoot, s.ServerFolder);
        WriteLog($"[{s.Name}] Restoring solution"); await RunProcess("dotnet", $"restore \"{Path.Combine(config.ApiPath, "CodeName399.sln")}\"", config.ApiPath, s.Name, true, operationCts!.Token);
        await RunProcess("dotnet", $"publish \"{project}\" -c Release -p:PublishProfile={s.PublishProfile}", config.ApiPath, s.Name, true, operationCts.Token);
        if (!File.Exists(Path.Combine(stage, s.Executable))) throw new Exception($"[{s.Name}] Expected executable missing: {stage}\\{s.Executable}");
        WriteLog($"[{s.Name}] Copying publish output to {dest}");
        Directory.CreateDirectory(dest);
        foreach (var pattern in new[] { "*.dll", "*.exe", "*.pdb", "*.deps.json", "*.runtimeconfig.json", "*.config" }) foreach (var f in Directory.EnumerateFiles(dest, pattern)) TryDelete(f);
        SafeDelete(Path.Combine(dest, "runtimes"));
        await Task.Run(() => CopyDirectory(stage, dest, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "appsettings.json", "appsettings.Production.json", "ocelot.json" }), operationCts!.Token);
        operationCts.Token.ThrowIfCancellationRequested();
        await StartService(s);
    }

    async Task BuildDeployUi()
    {
        if (operationCts != null) return;
        operationCts = new CancellationTokenSource(); SetBusy(true);
        try
        {
            WriteLog("=== BUILD & DEPLOY UI started ===");
            await DeployUi();
            WriteLog("=== BUILD & DEPLOY UI finished ===");
        }
        catch (Exception ex)
        {
            WriteLog(ex.Message, true);
            MessageBox.Show(ex.Message, "UI deployment failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            operationCts.Dispose(); operationCts = null; SetBusy(false); await RefreshStatusAsync();
        }
    }

    async Task DeployUi()
    {
        WriteLog("[UI] npm install / ci"); var npm = File.Exists(Path.Combine(config.AngularPath, "package-lock.json")) ? "ci" : "install"; await RunProcess("npm", npm, config.AngularPath, "UI", true, operationCts!.Token);
        await RunProcess("npm", "run build -- --configuration=production", config.AngularPath, "UI", true, operationCts.Token);
        string? src = new[] { Path.Combine(config.AngularPath, "dist", "CodeName399", "browser"), Path.Combine(config.AngularPath, "dist", "browser") }.FirstOrDefault(d => File.Exists(Path.Combine(d, "index.html")));
        if (src == null) src = Directory.Exists(Path.Combine(config.AngularPath, "dist")) ? Directory.EnumerateDirectories(Path.Combine(config.AngularPath, "dist")).FirstOrDefault(d => File.Exists(Path.Combine(d, "browser", "index.html"))) is string d ? Path.Combine(d, "browser") : null : null;
        if (src == null) throw new Exception("Angular browser output not found.");
        var dest = Path.Combine(config.ServerRoot, "UI"); Directory.CreateDirectory(dest); WriteLog($"[UI] Deploying to {dest}"); await Task.Run(() => MirrorDirectory(src, dest), operationCts!.Token);
        if (File.Exists(config.NginxExe))
        {
            var nginxDir = Path.GetDirectoryName(config.NginxExe)!;
            var test = await RunProcess(config.NginxExe, "-t", nginxDir, "Nginx", true, operationCts!.Token);
            if (test == 0)
            {
                WriteLog("[Nginx] Restarting Nginx after UI deployment...");
                await RunProcess(config.NginxExe, "-s quit", nginxDir, "Nginx", false, operationCts!.Token);
                await WaitForNginxStopped(15);
                await RunProcess(config.NginxExe, "", nginxDir, "Nginx", false, operationCts!.Token);
                await WaitForNginxStarted(15);
                WriteLog("[Nginx] Restart completed.");
            }
        }
        else
        {
            throw new Exception($"Nginx executable not found: {config.NginxExe}");
        }
    }

    async Task WaitForNginxStopped(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            if (!IsNginxRunning()) return;
            await Task.Delay(1000);
        }
        WriteLog("[Nginx] Existing process did not stop within timeout; starting a new process anyway.", true);
    }

    async Task WaitForNginxStarted(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            if (IsNginxRunning()) return;
            await Task.Delay(1000);
        }
        throw new Exception("Nginx did not start after UI deployment.");
    }

    bool IsNginxRunning() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(config.NginxExe)).Length > 0;

    async Task RunLocalAsync(ServiceInfo s)
    {
        if (localProcesses.ContainsKey(s.Name) && !localProcesses[s.Name].HasExited)
        {
            WriteLog($"[{s.Name}] Local process is already running (PID {localProcesses[s.Name].Id}).");
            return;
        }

        var project = Path.Combine(config.ApiPath, s.ProjectRelativePath);
        if (!File.Exists(project))
        {
            MessageBox.Show($"Project not found: {project}", $"Run {s.Name}", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Local development and the installed Windows Service cannot safely own the same port.
        // Stop only this service before starting its corresponding local process.
        try
        {
            await StopHostedServiceForLocalAsync(s);
        }
        catch (Exception ex)
        {
            WriteLog($"[{s.Name}] Local start cancelled: {ex.Message}", true);
            MessageBox.Show(ex.Message, $"Run {s.Name}", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var psi = new ProcessStartInfo("dotnet", $"run --project \"{project}\" -c Debug --no-launch-profile")
        {
            WorkingDirectory = config.ApiPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) WriteLog($"[{s.Name} LOCAL] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) WriteLog($"[{s.Name} LOCAL] {e.Data}", true); };
        process.Exited += (_, _) =>
        {
            if (InvokeRequired) BeginInvoke(() => LocalProcessExited(s.Name, process));
            else LocalProcessExited(s.Name, process);
        };

        try
        {
            if (!process.Start()) throw new Exception($"Unable to start local process for {s.Name}.");
            localProcesses[s.Name] = process;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            localRunButtons[s.Name].Enabled = false; localStopButtons[s.Name].Enabled = true;
            stateLabels[s.Name].Text = $"LOCAL STARTING • :{s.Port}";
            WriteLog($"[{s.Name}] Local development process started (PID {process.Id}) on expected TCP :{s.Port}.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    async Task StopHostedServiceForLocalAsync(ServiceInfo s)
    {
        var state = await GetServiceState(s.WindowsService);
        if (state is null)
        {
            WriteLog($"[{s.Name}] Windows service '{s.WindowsService}' is not installed. Starting local process directly.");
            return;
        }

        if (state is "STOPPED" or "STOP_PENDING")
        {
            WriteLog($"[{s.Name}] Hosted service '{s.WindowsService}' is already stopped.");
            return;
        }

        WriteLog($"[{s.Name}] Stopping hosted service '{s.WindowsService}' before local start (state: {state})...");
        var exitCode = await RunProcess("sc.exe", $"stop \"{s.WindowsService}\"", Environment.CurrentDirectory, s.Name + " LOCAL", false);
        if (exitCode != 0)
            throw new Exception($"[{s.Name}] Could not stop hosted service '{s.WindowsService}'. sc.exe exit code: {exitCode}.");

        await WaitService(s.WindowsService, "STOPPED", 45);
        WriteLog($"[{s.Name}] Hosted service '{s.WindowsService}' stopped. Starting local development process.");
    }

    void LocalProcessExited(string name, Process process)
    {
        if (localProcesses.TryGetValue(name, out var current) && ReferenceEquals(current, process)) localProcesses.Remove(name);
        if (localRunButtons.ContainsKey(name)) { localRunButtons[name].Enabled = true; localStopButtons[name].Enabled = false; }
        WriteLog($"[{name}] Local process exited with code {process.ExitCode}.", process.ExitCode != 0);
        try { process.Dispose(); } catch { }
        _ = RefreshStatusAsync();
    }

    void StopLocal(ServiceInfo s)
    {
        if (!localProcesses.TryGetValue(s.Name, out var process) || process.HasExited)
        {
            WriteLog($"[{s.Name}] No local process is running.");
            localRunButtons[s.Name].Enabled = true; localStopButtons[s.Name].Enabled = false;
            return;
        }

        try
        {
            WriteLog($"[{s.Name}] Stopping local process PID {process.Id}...");
            process.Kill(entireProcessTree: true);
            localProcesses.Remove(s.Name);
            localRunButtons[s.Name].Enabled = true; localStopButtons[s.Name].Enabled = false;
            stateLabels[s.Name].Text = "LOCAL STOPPED";
        }
        catch (Exception ex) { WriteLog($"[{s.Name}] Local stop failed: {ex.Message}", true); }
    }

    async Task StopSelected()
    {
        if (operationCts != null) return;
        var selected = Selected(); if (selected.Count == 0) { MessageBox.Show("Select at least one microservice.", "Stop Service", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        operationCts = new CancellationTokenSource(); SetBusy(true);
        try
        {
            WriteLog("=== STOP SERVICE started ===");
            await Task.WhenAll(selected.Select(async s =>
            {
                operationCts!.Token.ThrowIfCancellationRequested();
                await StopService(s);
            }));
            WriteLog("=== STOP SERVICE finished ===");
        }
        catch (OperationCanceledException) { WriteLog("=== STOP SERVICE cancelled ===", true); }
        catch (Exception ex) { WriteLog(ex.Message, true); MessageBox.Show(ex.Message, "Stop failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { operationCts.Dispose(); operationCts = null; SetBusy(false); await RefreshStatusAsync(); }
    }

    async Task RestartSelected()
    {
        if (operationCts != null) return;
        var selected = Selected(); if (selected.Count == 0) { MessageBox.Show("Select at least one microservice."); return; }
        operationCts = new CancellationTokenSource(); SetBusy(true);
        try
        {
            await Task.WhenAll(selected.Select(async s =>
            {
                operationCts!.Token.ThrowIfCancellationRequested();
                await StopService(s);
                await StartService(s);
            }));
        }
        catch (OperationCanceledException) { WriteLog("=== RESTART SERVICE cancelled ===", true); }
        catch (Exception ex) { WriteLog(ex.Message, true); MessageBox.Show(ex.Message, "Restart failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { operationCts.Dispose(); operationCts = null; SetBusy(false); await RefreshStatusAsync(); }
    }

    async Task StopService(ServiceInfo s)
    {
        var token = operationCts?.Token ?? CancellationToken.None;
        token.ThrowIfCancellationRequested();
        WriteLog($"[{s.Name}] Stopping {s.WindowsService}");
        await RunProcess("sc.exe", $"stop \"{s.WindowsService}\"", Environment.CurrentDirectory, s.Name, false, token);
        await WaitService(s.WindowsService, "STOPPED", 45, token);
    }
    async Task StartService(ServiceInfo s)
    {
        var token = operationCts?.Token ?? CancellationToken.None;
        token.ThrowIfCancellationRequested();
        WriteLog($"[{s.Name}] Starting {s.WindowsService} (TCP {s.Port})");
        await RunProcess("sc.exe", $"start \"{s.WindowsService}\"", Environment.CurrentDirectory, s.Name, false, token);
        await WaitService(s.WindowsService, "RUNNING", 60, token);
        await WaitPort(s.Port, 60, token);
    }

    async Task RefreshStatusAsync()
    {
        foreach (var s in services)
        {
            var localRunning = localProcesses.TryGetValue(s.Name, out var localProcess) && !localProcess.HasExited;
            if (localRunning)
            {
                var listeningLocal = await IsPortListening(s.Port);
                stateLabels[s.Name].Text = listeningLocal ? $"LOCAL RUNNING • :{s.Port}" : $"LOCAL RUNNING • :{s.Port} (starting)";
                localRunButtons[s.Name].Enabled = false; localStopButtons[s.Name].Enabled = true;
                continue;
            }
            localRunButtons[s.Name].Enabled = true; localStopButtons[s.Name].Enabled = false;
            var state = await GetServiceState(s.WindowsService); var listening = await IsPortListening(s.Port);
            if (state == "RUNNING" && listening) stateLabels[s.Name].Text = $"SERVICE RUNNING • :{s.Port}";
            else if (state == "RUNNING") stateLabels[s.Name].Text = $"RUNNING • port down";
            else stateLabels[s.Name].Text = state ?? "NOT INSTALLED";
        }
        if (uiStateLabel != null)
            uiStateLabel.Text = File.Exists(config.NginxExe) ? "Nginx configured" : "Nginx not found";
    }

    async Task<string?> GetServiceState(string name) => await Task.Run(() => { try { using var sc = new ServiceController(name); return sc.Status.ToString().ToUpperInvariant(); } catch { return null; } });
    async Task WaitService(string name, string wanted, int seconds, CancellationToken cancellationToken = default) { for (int i = 0; i < seconds; i++) { cancellationToken.ThrowIfCancellationRequested(); var s = await GetServiceState(name); if (s == wanted) return; await Task.Delay(1000, cancellationToken); } throw new Exception($"Service {name} did not reach {wanted}."); }
    async Task WaitPort(int port, int seconds, CancellationToken cancellationToken = default) { for (int i = 0; i < seconds; i++) { cancellationToken.ThrowIfCancellationRequested(); if (await IsPortListening(port)) return; await Task.Delay(1000, cancellationToken); } throw new Exception($"TCP {port} is not listening."); }
    async Task<bool> IsPortListening(int port) => await Task.Run(() => System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port));

    async Task RunCustomCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            MessageBox.Show("Enter a command to run.", "Custom Command",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (operationCts != null)
        {
            MessageBox.Show("Another operation is already running. Cancel it first.",
                "Operation in progress", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        operationCts = new CancellationTokenSource();
        SetBusy(true);

        try
        {
            WriteLog($"=== CUSTOM COMMAND started: {command} ===");
            await RunProcess("cmd.exe", $"/d /c {command}",
                Environment.CurrentDirectory, "CUSTOM");
            WriteLog("=== CUSTOM COMMAND finished ===");
        }
        catch (OperationCanceledException)
        {
            WriteLog("=== CUSTOM COMMAND cancelled ===", true);
        }
        catch (Exception ex)
        {
            WriteLog(ex.Message, true);
            MessageBox.Show(ex.Message, "Custom Command failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            operationCts.Dispose();
            operationCts = null;
            SetBusy(false);
            await RefreshStatusAsync();
        }
    }

    async Task<int> RunProcess(string exe, string args, string cwd, string tag, bool throwOnError = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteLog($"[{tag}] > {exe} {args}");
        var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) WriteLog($"[{tag}] {e.Data}"); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) WriteLog($"[{tag}] {e.Data}", true); };
        if (!p.Start()) throw new Exception($"Unable to start {exe}");
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { await p.WaitForExitAsync(); } catch { }
            WriteLog($"[{tag}] Process cancelled and terminated: {exe}", true);
            throw;
        }
        if (p.ExitCode != 0 && throwOnError) throw new Exception($"[{tag}] {exe} failed with exit code {p.ExitCode}."); return p.ExitCode;
    }

    static void SafeDelete(string path) { if (Directory.Exists(path)) try { Directory.Delete(path, true); } catch { } }
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    static void CopyDirectory(string src, string dest, HashSet<string> excluded) { foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(src, dest)); foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories)) { if (excluded.Contains(Path.GetFileName(f))) continue; var target = f.Replace(src, dest); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(f, target, true); } }
    static void MirrorDirectory(string src, string dest) { foreach (var f in Directory.GetFiles(dest, "*", SearchOption.AllDirectories)) TryDelete(f); foreach (var d in Directory.GetDirectories(dest, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length)) SafeDelete(d); CopyDirectory(src, dest, new HashSet<string>()); }
}
