
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SettingsForm : Form
{
    private readonly RunnerSettings _settings;
    private readonly Dictionary<BrowserProfileSettings, string> _originalProfileNames = new();

    private bool _isPopulating;
    private bool _isRunning;

    // General tab
    private TextBox _targetUrl = null!;
    private CheckBox _headlessCheck = null!;
    private ComboBox _browserCombo = null!;
    private TextBox _screenshotPath = null!;
    private NumericUpDown _timeoutUpDown = null!;

    // Profiles tab
    private ListBox _profileList = null!;
    private TextBox _profileNameBox = null!;
    private TextBox _profileDirBox = null!;
    private TextBox _profileUserBox = null!;
    private TextBox _profilePassBox = null!;
    private CheckBox _profileProxyEnabled = null!;
    private TextBox _profileProxyServer = null!;
    private TextBox _profileProxyUser = null!;
    private TextBox _profileProxyPass = null!;
    private BrowserProfileSettings? _activeProfile;

    // Tasks tab
    private ListBox _taskList = null!;
    private TextBox _taskNameBox = null!;
    private CheckBox _taskEnabledCheck = null!;
    private ComboBox _taskProfileCombo = null!;
    private ComboBox _taskRunModeCombo = null!;
    private NumericUpDown _taskDelayMinutes = null!;
    private DateTimePicker _taskDailyTimePicker = null!;
    private CheckBox _taskRepeatDailyCheck = null!;
    private CheckBox _taskUseCredentialCheck = null!;
    private TextBox _taskTargetOverride = null!;
    private TextBox _taskScreenshotOverride = null!;
    private ProfileTaskSettings? _activeTask;

    // Status view
    private Label _statusMessageLabel = null!;
    private ListView _taskStatusList = null!;
    private readonly Dictionary<Guid, ListViewItem> _statusItems = new();

    private Button _runButton = null!;

    private readonly List<BrowserChoice> _browserChoices = new()
    {
        new BrowserChoice("Google Chrome", BrowserChannels.Chrome),
        new BrowserChoice("Microsoft Edge", BrowserChannels.Edge)
    };

    public event Func<RunnerSettings, Task>? RunRequested;

    public SettingsForm(RunnerSettings settings)
    {
        _settings = settings;

        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "BOTRTN Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Padding = new Padding(12);
        ClientSize = new Size(820, 860);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateGeneralTab());
        tabs.TabPages.Add(CreateProfilesTab());
        tabs.TabPages.Add(CreateTasksTab());
        Controls.Add(tabs);

        AcceptButton = CreateBottomButtons(out var cancelButton);
        CancelButton = cancelButton;

        PopulateControls();
    }

    private TabPage CreateGeneralTab()
    {
        var page = new TabPage("General");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        AddField(layout, "Target URL:", _targetUrl = CreateTextBox());

        _headlessCheck = new CheckBox { Text = "Run headless (ไม่แสดงหน้าต่างเบราว์เซอร์)", AutoSize = true };
        AddControlRow(layout, _headlessCheck);

        _browserCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _browserCombo.Items.AddRange(_browserChoices.Cast<object>().ToArray());
        AddField(layout, "Browser:", _browserCombo);

        AddField(layout, "Screenshot path:", _screenshotPath = CreateTextBox());

        _timeoutUpDown = new NumericUpDown
        {
            Minimum = 1000,
            Maximum = 600000,
            Increment = 1000,
            Dock = DockStyle.Left,
            Width = 140
        };
        AddField(layout, "Timeout (ms):", _timeoutUpDown);

        layout.Controls.Add(new Label(), 0, layout.RowCount);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        page.Controls.Add(layout);
        return page;
    }

    private TabPage CreateProfilesTab()
    {
        var page = new TabPage("Profiles");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _profileList = new ListBox { Dock = DockStyle.Fill };
        _profileList.SelectedIndexChanged += (_, _) => OnProfileSelectionChanged();
        layout.Controls.Add(_profileList, 0, 0);

        var profileButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };
        var addProfile = new Button { Text = "เพิ่มโปรไฟล์", AutoSize = true };
        addProfile.Click += (_, _) => AddProfile();
        var removeProfile = new Button { Text = "ลบโปรไฟล์", AutoSize = true };
        removeProfile.Click += (_, _) => RemoveProfile();
        profileButtons.Controls.Add(addProfile);
        profileButtons.Controls.Add(removeProfile);
        layout.Controls.Add(profileButtons, 0, 1);
        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6)
        };
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        AddField(detailLayout, "ชื่อโปรไฟล์:", _profileNameBox = CreateTextBox());
        _profileNameBox.TextChanged += (_, _) => UpdateActiveProfileName();

        AddField(detailLayout, "โฟลเดอร์โปรไฟล์:", _profileDirBox = CreateTextBox());
        _profileDirBox.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeProfile == null) return;
            _activeProfile.UserDataDirName = _profileDirBox.Text.Trim();
        };

        var credGroup = CreateGroup("Credentials", detailLayout, out var credPanel);
        AddField(credPanel, "Username:", _profileUserBox = CreateTextBox());
        _profileUserBox.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeProfile == null) return;
            _activeProfile.Credentials.User = _profileUserBox.Text.Trim();
        };
        AddField(credPanel, "Password:", _profilePassBox = CreatePasswordBox());
        _profilePassBox.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeProfile == null) return;
            _activeProfile.Credentials.Pass = _profilePassBox.Text;
        };

        var proxyGroup = CreateGroup("Proxy", detailLayout, out var proxyPanel);
        _profileProxyEnabled = new CheckBox { Text = "Enable proxy", AutoSize = true };
        _profileProxyEnabled.CheckedChanged += (_, _) =>
        {
            if (_isPopulating || _activeProfile == null) return;
            _activeProfile.Proxy.Enabled = _profileProxyEnabled.Checked;
            ToggleProfileProxyInputs();
        };
        AddControlRow(proxyPanel, _profileProxyEnabled);

        AddField(proxyPanel, "Server:", _profileProxyServer = CreateTextBox());
        _profileProxyServer.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeProfile == null) return;
            _activeProfile.Proxy.Server = _profileProxyServer.Text.Trim();
        };

        AddField(proxyPanel, "Username:", _profileProxyUser = CreateTextBox());
        _profileProxyUser.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeProfile == null) return;
            _activeProfile.Proxy.Username = _profileProxyUser.Text.Trim();
        };

        AddField(proxyPanel, "Password:", _profileProxyPass = CreatePasswordBox());
        _profileProxyPass.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeProfile == null) return;
            _activeProfile.Proxy.Password = _profileProxyPass.Text;
        };

        layout.Controls.Add(detailLayout, 1, 0);
        layout.SetRowSpan(detailLayout, 2);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage CreateTasksTab()
    {
        var page = new TabPage("Tasks");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35f));

        _taskList = new ListBox { Dock = DockStyle.Fill };
        _taskList.SelectedIndexChanged += (_, _) => OnTaskSelectionChanged();
        layout.Controls.Add(_taskList, 0, 0);

        var taskButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };
        var addTask = new Button { Text = "เพิ่ม Task", AutoSize = true };
        addTask.Click += (_, _) => AddTask();
        var copyTask = new Button { Text = "คัดลอก Task", AutoSize = true };
        copyTask.Click += (_, _) => CopyTask();
        var removeTask = new Button { Text = "ลบ Task", AutoSize = true };
        removeTask.Click += (_, _) => RemoveTask();
        taskButtons.Controls.Add(addTask);
        taskButtons.Controls.Add(copyTask);
        taskButtons.Controls.Add(removeTask);
        layout.Controls.Add(taskButtons, 0, 1);
        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6)
        };
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        AddField(detailLayout, "ชื่องาน:", _taskNameBox = CreateTextBox());
        _taskNameBox.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            _activeTask.Name = _taskNameBox.Text.Trim();
            RefreshTaskList(_activeTask);
        };

        _taskEnabledCheck = new CheckBox { Text = "เปิดใช้งาน Task", AutoSize = true };
        _taskEnabledCheck.CheckedChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            _activeTask.Enabled = _taskEnabledCheck.Checked;
            RefreshTaskList(_activeTask);
        };
        AddControlRow(detailLayout, _taskEnabledCheck);

        AddField(detailLayout, "ใช้โปรไฟล์:", _taskProfileCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill });
        _taskProfileCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            if (_taskProfileCombo.SelectedItem is string name)
                _activeTask.ProfileName = name;
            RefreshTaskList(_activeTask);
        };

        AddField(detailLayout, "Run mode:", _taskRunModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill });
        _taskRunModeCombo.Items.AddRange(Enum.GetNames(typeof(TaskRunMode)));
        _taskRunModeCombo.SelectedIndexChanged += (_, _) => UpdateTaskRunMode();

        _taskDelayMinutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1440,
            Increment = 1,
            Dock = DockStyle.Left,
            Width = 120
        };
        _taskDelayMinutes.ValueChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            _activeTask.Delay = TimeSpan.FromMinutes((double)_taskDelayMinutes.Value);
        };
        AddField(detailLayout, "Delay (minutes):", _taskDelayMinutes);

        _taskDailyTimePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            Width = 110
        };
        _taskDailyTimePicker.ValueChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            _activeTask.RunAtTime = _taskDailyTimePicker.Value.TimeOfDay;
        };
        AddField(detailLayout, "เริ่มทำงานเวลา:", _taskDailyTimePicker);

        _taskRepeatDailyCheck = new CheckBox { Text = "ทำซ้ำทุกวัน (เฉพาะ DailyTime)", AutoSize = true };
        _taskRepeatDailyCheck.CheckedChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            _activeTask.RepeatDaily = _taskRepeatDailyCheck.Checked;
        };
        AddControlRow(detailLayout, _taskRepeatDailyCheck);

        _taskUseCredentialCheck = new CheckBox { Text = "ใช้ credential สำหรับล็อกอิน", AutoSize = true };
        _taskUseCredentialCheck.CheckedChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            _activeTask.UseCredentials = _taskUseCredentialCheck.Checked;
        };
        AddControlRow(detailLayout, _taskUseCredentialCheck);

        AddField(detailLayout, "Target URL override:", _taskTargetOverride = CreateTextBox());
        _taskTargetOverride.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            _activeTask.TargetUrlOverride = string.IsNullOrWhiteSpace(_taskTargetOverride.Text)
                ? null
                : _taskTargetOverride.Text.Trim();
        };

        AddField(detailLayout, "Screenshot path override:", _taskScreenshotOverride = CreateTextBox());
        _taskScreenshotOverride.TextChanged += (_, _) =>
        {
            if (_isPopulating || _activeTask == null) return;
            _activeTask.ScreenshotPathOverride = string.IsNullOrWhiteSpace(_taskScreenshotOverride.Text)
                ? null
                : _taskScreenshotOverride.Text.Trim();
        };

        layout.Controls.Add(detailLayout, 1, 0);

        var statusGroup = new GroupBox { Text = "สถานะ Task", Dock = DockStyle.Fill };
        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _statusMessageLabel = new Label
        {
            Text = "กด \"บันทึกและเริ่มทำงาน\" เพื่อเริ่ม",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 3, 3, 6)
        };
        statusLayout.Controls.Add(_statusMessageLabel, 0, 0);

        _taskStatusList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _taskStatusList.Columns.Add("Task", 220);
        _taskStatusList.Columns.Add("สถานะ", 320);
        statusLayout.Controls.Add(_taskStatusList, 0, 1);

        statusGroup.Controls.Add(statusLayout);
        layout.Controls.Add(statusGroup, 1, 1);

        page.Controls.Add(layout);
        return page;
    }

    private IButtonControl CreateBottomButtons(out IButtonControl cancelButton)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
            Margin = new Padding(0, 12, 0, 0)
        };

        _runButton = new Button
        {
            Text = "บันทึกและเริ่มทำงาน",
            AutoSize = true
        };
        _runButton.Click += RunButtonOnClickAsync;

        var cancel = new Button
        {
            Text = "ยกเลิก",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };

        panel.Controls.Add(_runButton);
        panel.Controls.Add(cancel);
        Controls.Add(panel);

        cancelButton = cancel;
        return _runButton;
    }
    private async void RunButtonOnClickAsync(object? sender, EventArgs e)
    {
        if (_isRunning)
            return;

        if (!TryApplyChanges(out var error))
        {
            MessageBox.Show(this, error, "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SettingsManager.Save(_settings);

        if (RunRequested is null)
        {
            MessageBox.Show(this, "ไม่พบ Runner สำหรับประมวลผล", "Cannot start", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var clone = SettingsManager.Clone(_settings);

        SetRunState(true);
        try
        {
            await RunRequested.Invoke(clone);
        }
        finally
        {
            SetRunState(false);
        }
    }

    public void InitializeTaskStatuses(IEnumerable<ProfileTaskSettings> tasks)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<IEnumerable<ProfileTaskSettings>>(InitializeTaskStatuses), tasks);
            return;
        }

        _statusItems.Clear();
        _taskStatusList.Items.Clear();

        var list = tasks.ToList();
        if (list.Count == 0)
        {
            ShowStatusMessage("ไม่มี Task ที่เปิดใช้งาน");
            return;
        }

        ShowStatusMessage("กำลังเตรียมรัน Task...");

        foreach (var task in list)
        {
            var item = new ListViewItem(task.Name) { Tag = task.Id };
            item.SubItems.Add("รอเริ่ม...");
            _statusItems[task.Id] = item;
            _taskStatusList.Items.Add(item);
        }
    }

    public void UpdateTaskStatus(Guid taskId, string status, bool isError = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<Guid, string, bool>(UpdateTaskStatus), taskId, status, isError);
            return;
        }

        if (_statusItems.TryGetValue(taskId, out var item))
        {
            item.SubItems[1].Text = status;
            item.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        }
    }

    public void ShowStatusMessage(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(ShowStatusMessage), message);
            return;
        }

        _statusMessageLabel.Text = message;
    }
    private void PopulateControls()
    {
        _isPopulating = true;

        _targetUrl.Text = _settings.TargetUrl;
        _headlessCheck.Checked = _settings.Headless;
        _browserCombo.SelectedItem = _browserChoices.FirstOrDefault(c => c.Channel == _settings.Browser) ?? _browserChoices[0];
        _screenshotPath.Text = _settings.ScreenshotPath;
        _timeoutUpDown.Value = Math.Min(Math.Max(_settings.Timeout, (int)_timeoutUpDown.Minimum), (int)_timeoutUpDown.Maximum);

        RefreshProfileList(null);
        RefreshTaskList(null);
        PopulateTaskProfileCombo();

        _isPopulating = false;
    }

    private void RefreshProfileList(BrowserProfileSettings? selectProfile)
    {
        _profileList.BeginUpdate();
        _profileList.Items.Clear();
        foreach (var profile in _settings.Profiles)
        {
            _profileList.Items.Add(profile);
            if (!_originalProfileNames.ContainsKey(profile))
                _originalProfileNames[profile] = profile.Name;
        }
        _profileList.EndUpdate();

        selectProfile ??= _settings.Profiles.FirstOrDefault(p => string.Equals(p.Name, _settings.SelectedProfile, StringComparison.Ordinal))
                        ?? _settings.Profiles.First();

        _profileList.SelectedItem = selectProfile;
        _activeProfile = selectProfile;
        LoadProfileDetail(selectProfile);
    }

    private void RefreshTaskList(ProfileTaskSettings? selectTask)
    {
        _taskList.BeginUpdate();
        _taskList.Items.Clear();
        foreach (var task in _settings.Tasks)
            _taskList.Items.Add(task);
        _taskList.EndUpdate();

        if (_settings.Tasks.Count == 0)
        {
            _activeTask = null;
            return;
        }

        selectTask ??= _settings.Tasks[0];
        _taskList.SelectedItem = selectTask;
        _activeTask = selectTask;
        LoadTaskDetail(selectTask);
    }

    private void LoadProfileDetail(BrowserProfileSettings profile)
    {
        _isPopulating = true;

        _profileNameBox.Text = profile.Name;
        _profileDirBox.Text = profile.UserDataDirName;
        _profileUserBox.Text = profile.Credentials.User ?? string.Empty;
        _profilePassBox.Text = profile.Credentials.Pass ?? string.Empty;
        _profileProxyEnabled.Checked = profile.Proxy.Enabled;
        _profileProxyServer.Text = profile.Proxy.Server ?? string.Empty;
        _profileProxyUser.Text = profile.Proxy.Username ?? string.Empty;
        _profileProxyPass.Text = profile.Proxy.Password ?? string.Empty;
        ToggleProfileProxyInputs();

        _isPopulating = false;
    }

    private void LoadTaskDetail(ProfileTaskSettings task)
    {
        _isPopulating = true;

        _taskNameBox.Text = task.Name;
        _taskEnabledCheck.Checked = task.Enabled;
        _taskProfileCombo.SelectedItem = task.ProfileName;
        _taskRunModeCombo.SelectedItem = task.RunMode.ToString();
        _taskUseCredentialCheck.Checked = task.UseCredentials;
        _taskTargetOverride.Text = task.TargetUrlOverride ?? string.Empty;
        _taskScreenshotOverride.Text = task.ScreenshotPathOverride ?? string.Empty;
        _taskRepeatDailyCheck.Checked = task.RepeatDaily;
        _taskDelayMinutes.Value = (decimal)Math.Clamp(task.Delay?.TotalMinutes ?? 1, 1, 1440);
        _taskDailyTimePicker.Value = DateTime.Today.Add(task.RunAtTime ?? TimeSpan.FromHours(9));
        ToggleTaskRunModeControls(task.RunMode);

        _isPopulating = false;
    }

    private void ToggleProfileProxyInputs()
    {
        var enabled = _profileProxyEnabled.Checked;
        _profileProxyServer.Enabled = enabled;
        _profileProxyUser.Enabled = enabled;
        _profileProxyPass.Enabled = enabled;
    }

    private void ToggleTaskRunModeControls(TaskRunMode mode)
    {
        _taskDelayMinutes.Enabled = mode == TaskRunMode.Delay;
        _taskDailyTimePicker.Enabled = mode == TaskRunMode.DailyTime;
        _taskRepeatDailyCheck.Enabled = mode == TaskRunMode.DailyTime;
    }
    private void UpdateTaskRunMode()
    {
        if (_isPopulating || _activeTask == null)
            return;

        if (_taskRunModeCombo.SelectedItem is not string text || !Enum.TryParse<TaskRunMode>(text, out var mode))
            return;

        _activeTask.RunMode = mode;
        if (mode == TaskRunMode.Delay)
        {
            _activeTask.Delay ??= TimeSpan.FromMinutes((double)_taskDelayMinutes.Value);
            _activeTask.RunAtTime = null;
        }
        else if (mode == TaskRunMode.DailyTime)
        {
            _activeTask.RunAtTime ??= _taskDailyTimePicker.Value.TimeOfDay;
            if (!_activeTask.RepeatDaily)
                _activeTask.RepeatDaily = true;
            _activeTask.Delay = null;
        }
        else
        {
            _activeTask.Delay = null;
            _activeTask.RunAtTime = null;
        }

        ToggleTaskRunModeControls(mode);
    }

    private void OnProfileSelectionChanged()
    {
        if (_isPopulating)
            return;

        if (_profileList.SelectedItem is BrowserProfileSettings profile)
        {
            _activeProfile = profile;
            _settings.SelectedProfile = profile.Name;
            LoadProfileDetail(profile);
            PopulateTaskProfileCombo();
        }
    }

    private void OnTaskSelectionChanged()
    {
        if (_isPopulating)
            return;

        if (_taskList.SelectedItem is ProfileTaskSettings task)
        {
            _activeTask = task;
            LoadTaskDetail(task);
        }
    }

    private void AddProfile()
    {
        var name = GenerateUniqueName("Profile", _settings.Profiles.Select(p => p.Name));
        var profile = new BrowserProfileSettings
        {
            Name = name,
            UserDataDirName = $"botRTN_{Sanitize(name)}",
            Credentials = new CredentialSettings(),
            Proxy = new ProxySettings()
        };
        _settings.Profiles.Add(profile);
        _originalProfileNames[profile] = profile.Name;
        RefreshProfileList(profile);
        PopulateTaskProfileCombo();
    }

    private void RemoveProfile()
    {
        if (_settings.Profiles.Count <= 1)
        {
            MessageBox.Show(this, "ต้องมีอย่างน้อย 1 โปรไฟล์", "ไม่สามารถลบได้", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_activeProfile == null)
            return;

        var index = _settings.Profiles.IndexOf(_activeProfile);
        _settings.Profiles.Remove(_activeProfile);
        _originalProfileNames.Remove(_activeProfile);

        foreach (var task in _settings.Tasks.Where(t => t.ProfileName == _activeProfile.Name))
            task.ProfileName = _settings.Profiles[0].Name;

        var next = _settings.Profiles[Math.Max(0, index - 1)];
        RefreshProfileList(next);
        PopulateTaskProfileCombo();
    }

    private void AddTask()
    {
        var name = GenerateUniqueName("Task", _settings.Tasks.Select(t => t.Name));
        var profile = _settings.Profiles.First();
        var task = new ProfileTaskSettings
        {
            ProfileName = profile.Name,
            Name = name,
            Enabled = true,
            RunMode = TaskRunMode.Immediate,
            UseCredentials = true
        };
        _settings.Tasks.Add(task);
        RefreshTaskList(task);
    }

    private void CopyTask()
    {
        if (_activeTask == null)
            return;

        var clone = new ProfileTaskSettings
        {
            Id = Guid.NewGuid(),
            Name = GenerateUniqueName(_activeTask.Name, _settings.Tasks.Select(t => t.Name)),
            ProfileName = _activeTask.ProfileName,
            Enabled = _activeTask.Enabled,
            RunMode = _activeTask.RunMode,
            Delay = _activeTask.Delay,
            RunAtTime = _activeTask.RunAtTime,
            RepeatDaily = _activeTask.RepeatDaily,
            UseCredentials = _activeTask.UseCredentials,
            TargetUrlOverride = _activeTask.TargetUrlOverride,
            ScreenshotPathOverride = _activeTask.ScreenshotPathOverride
        };
        _settings.Tasks.Add(clone);
        RefreshTaskList(clone);
    }
    private void RemoveTask()
    {
        if (_activeTask == null)
            return;

        var index = _settings.Tasks.IndexOf(_activeTask);
        _settings.Tasks.Remove(_activeTask);
        var next = index >= 0 && index < _settings.Tasks.Count ? _settings.Tasks[index] : _settings.Tasks.LastOrDefault();
        RefreshTaskList(next);
    }

    private void UpdateActiveProfileName()
    {
        if (_isPopulating || _activeProfile == null)
            return;

        var newName = _profileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(_activeProfile.Name, newName, StringComparison.Ordinal))
            return;

        var originalName = _originalProfileNames.TryGetValue(_activeProfile, out var orig) ? orig : _activeProfile.Name;
        _activeProfile.Name = newName;
        _originalProfileNames[_activeProfile] = newName;

        if (!string.IsNullOrWhiteSpace(originalName) && !string.Equals(originalName, newName, StringComparison.Ordinal))
        {
            foreach (var task in _settings.Tasks.Where(t => string.Equals(t.ProfileName, originalName, StringComparison.Ordinal)))
                task.ProfileName = newName;
            PopulateTaskProfileCombo();
        }

        RefreshProfileList(_activeProfile);
        RefreshTaskList(_activeTask);
    }

    private void PopulateTaskProfileCombo()
    {
        _isPopulating = true;
        var names = _settings.Profiles.Select(p => p.Name).ToArray();
        _taskProfileCombo.Items.Clear();
        _taskProfileCombo.Items.AddRange(names);
        if (_activeTask != null)
        {
            if (!names.Contains(_activeTask.ProfileName))
                _activeTask.ProfileName = names.FirstOrDefault() ?? _activeTask.ProfileName;
            _taskProfileCombo.SelectedItem = _activeTask.ProfileName;
        }
        _isPopulating = false;
    }

    private bool TryApplyChanges(out string error)
    {
        error = string.Empty;

        var url = _targetUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "กรุณากรอก Target URL ที่ถูกต้อง (ต้องเริ่มด้วย http หรือ https).";
            return false;
        }

        var screenshot = _screenshotPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(screenshot))
        {
            error = "กรุณาระบุ path สำหรับบันทึก screenshot.";
            return false;
        }

        if (_browserCombo.SelectedItem is not BrowserChoice browserChoice)
        {
            error = "กรุณาเลือก browser ที่รองรับ.";
            return false;
        }

        foreach (var profile in _settings.Profiles)
        {
            if (!ValidateProfile(profile, out error))
                return false;
        }

        foreach (var task in _settings.Tasks)
        {
            if (!ValidateTask(task, out error))
                return false;
        }

        _settings.TargetUrl = url;
        _settings.Headless = _headlessCheck.Checked;
        _settings.Browser = browserChoice.Channel;
        _settings.ScreenshotPath = screenshot;
        _settings.Timeout = (int)_timeoutUpDown.Value;

        if (_activeProfile != null)
            _settings.SelectedProfile = _activeProfile.Name;

        return true;
    }

    private bool ValidateProfile(BrowserProfileSettings profile, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            error = "ชื่อโปรไฟล์ต้องไม่ว่าง.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.UserDataDirName))
        {
            error = $"กรุณากรอกชื่อโฟลเดอร์สำหรับโปรไฟล์ \"{profile.Name}\".";
            return false;
        }

        return true;
    }

    private bool ValidateTask(ProfileTaskSettings task, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(task.Name))
        {
            error = "ชื่องานต้องไม่ว่าง.";
            return false;
        }

        if (!_settings.Profiles.Any(p => string.Equals(p.Name, task.ProfileName, StringComparison.Ordinal)))
        {
            error = $"Task \"{task.Name}\" ยังไม่ได้เลือกโปรไฟล์ที่ถูกต้อง.";
            return false;
        }

        if (task.RunMode == TaskRunMode.Delay && (task.Delay is null || task.Delay.Value <= TimeSpan.Zero))
        {
            error = $"Task \"{task.Name}\" ต้องกำหนด Delay (เป็นนาทีมากกว่า 0).";
            return false;
        }

        if (task.RunMode == TaskRunMode.DailyTime && task.RunAtTime is null)
        {
            error = $"Task \"{task.Name}\" ต้องกำหนดเวลาเริ่มทำงาน.";
            return false;
        }

        if (task.UseCredentials)
        {
            var profile = _settings.Profiles.First(p => string.Equals(p.Name, task.ProfileName, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(profile.Credentials.User) || string.IsNullOrWhiteSpace(profile.Credentials.Pass))
            {
                error = $"Task \"{task.Name}\" ต้องการ credential แต่โปรไฟล์ \"{profile.Name}\" ไม่มี Username/Password.";
                return false;
            }
        }

        return true;
    }
    private void SetRunState(bool running)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<bool>(SetRunState), running);
            return;
        }

        _isRunning = running;
        _runButton.Enabled = !running;
        UseWaitCursor = running;
    }

    private static TextBox CreateTextBox() => new() { Dock = DockStyle.Fill };

    private static TextBox CreatePasswordBox() => new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };

    private static void AddField(TableLayoutPanel layout, string labelText, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = labelText,
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(0, 6, 6, 6)
        };
        control.Margin = new Padding(0, 3, 0, 3);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private static void AddControlRow(TableLayoutPanel layout, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 3, 0, 3);
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }

    private static GroupBox CreateGroup(string title, TableLayoutPanel parent, out TableLayoutPanel inner)
    {
        inner = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6)
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6)
        };
        group.Controls.Add(inner);

        AddControlRow(parent, group);
        return group;
    }

    private static string GenerateUniqueName(string baseName, IEnumerable<string> existing)
    {
        var sanitized = string.IsNullOrWhiteSpace(baseName) ? "Item" : baseName.Trim();
        var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var candidate = sanitized;
        var suffix = 1;
        while (names.Contains(candidate))
            candidate = $"{sanitized} {++suffix}";
        return candidate;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Profile";
        var cleaned = Regex.Replace(value.Trim(), @"[^\w\-]+", "_");
        return string.IsNullOrWhiteSpace(cleaned) ? "Profile" : cleaned;
    }

    private sealed record BrowserChoice(string Display, string Channel)
    {
        public override string ToString() => Display;
    }
}
