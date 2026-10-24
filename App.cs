using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

[assembly: AssemblyTitle("随译 · Lingo Float")]
[assembly: AssemblyDescription("轻巧的置顶翻译窗口")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace LingoFloat
{
    public static class Ui
    {
        public static Window Load(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LingoFloat." + name + ".xaml"))
                return (Window)XamlReader.Load(stream);
        }
        public static T Get<T>(Window window, string name) where T : FrameworkElement { return (T)window.FindName(name); }
        public static Brush Color(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
    }

    public sealed class SettingsDialog
    {
        private readonly Window window;
        private readonly LocalStore store;
        private List<ModelInfo> models;
        private string modelsIdentity;
        private CancellationTokenSource modelRequest;
        private bool closed;
        public bool Saved { get; private set; }
        public SettingsDialog(Window owner, LocalStore storage)
        {
            store = storage;
            window = Ui.Load("Settings");
            if (owner != null) { window.Owner = owner; window.Topmost = owner.Topmost; }
            var s = store.Settings;
            models = s.Models ?? new List<ModelInfo>();
            modelsIdentity = s.CatalogIdentity;
            Ui.Get<TextBox>(window, "BaseBox").Text = s.BaseUrl;
            Ui.Get<ComboBox>(window, "ModelBox").ItemsSource = models;
            Ui.Get<ComboBox>(window, "ModelBox").Text = s.Model;
            try { Ui.Get<PasswordBox>(window, "KeyBox").Password = Secrets.Unprotect(s.ProtectedKey); }
            catch { Error("原密钥无法解密，请重新填写。" ); }
            Ui.Get<ComboBox>(window, "ProtocolBox").SelectedIndex = s.Protocol == "responses" ? 1 : 0;
            Ui.Get<ComboBox>(window, "TokenBox").SelectedIndex = s.TokenParameter == "max_tokens" ? 1 : s.TokenParameter == "none" ? 2 : 0;
            Ui.Get<TextBox>(window, "LimitBox").Text = s.MaxOutputTokens.ToString();
            Ui.Get<CheckBox>(window, "UsageCheck").IsChecked = s.IncludeUsage;
            Ui.Get<CheckBox>(window, "CacheCheck").IsChecked = s.CacheEnabled;
            Ui.Get<CheckBox>(window, "PassOnlyCheck").IsChecked = s.SubscriptionOnly;
            Ui.Get<CheckBox>(window, "ThinkingCheck").IsChecked = s.DisableThinking;
            Ui.Get<Button>(window, "FetchModelsButton").Click += async (o, e) => await FetchModels();
            Ui.Get<Button>(window, "TestModelButton").Click += async (o, e) => await TestModel();
            window.Closed += (o, e) => { closed = true; if (modelRequest != null) modelRequest.Cancel(); };
            Ui.Get<TextBox>(window, "BaseBox").TextChanged += (o, e) => UpdateProtocol();
            Ui.Get<Button>(window, "SaveButton").Click += Save;
            Ui.Get<Button>(window, "CancelButton").Click += (o, e) => window.Close();
            Ui.Get<Button>(window, "ClearCacheButton").Click += (o, e) =>
            {
                try { store.ClearCache(); Error("已清空本地缓存。" ); }
                catch { Error("无法清空缓存，请检查数据目录是否可写。" ); }
            };
            Ui.Get<ComboBox>(window, "ProtocolBox").SelectionChanged += (o, e) => UpdateProtocol();
            UpdateProtocol();
        }
        private void Error(string text) { Ui.Get<TextBlock>(window, "ErrorLabel").Text = text; Ui.Get<TextBlock>(window, "ErrorLabel").ToolTip = text; }
        private void UpdateProtocol()
        {
            var chat = Ui.Get<ComboBox>(window, "ProtocolBox").SelectedIndex == 0;
            Ui.Get<ComboBox>(window, "TokenBox").IsEnabled = chat;
            Ui.Get<CheckBox>(window, "UsageCheck").IsEnabled = chat;
            Ui.Get<CheckBox>(window, "ThinkingCheck").IsEnabled = chat;
            try { Ui.Get<CheckBox>(window, "PassOnlyCheck").IsEnabled = ModelCatalog.IsCline(FormSettings()); }
            catch { Ui.Get<CheckBox>(window, "PassOnlyCheck").IsEnabled = false; }
        }
        private Settings FormSettings()
        {
            var s = store.Settings.Copy();
            s.BaseUrl = Ui.Get<TextBox>(window, "BaseBox").Text.Trim();
            s.Model = Ui.Get<ComboBox>(window, "ModelBox").Text.Trim();
            s.Protocol = Ui.Get<ComboBox>(window, "ProtocolBox").SelectedIndex == 1 ? "responses" : "chat";
            s.TokenParameter = new[] { "max_completion_tokens", "max_tokens", "none" }[Math.Max(0, Ui.Get<ComboBox>(window, "TokenBox").SelectedIndex)];
            s.IncludeUsage = Ui.Get<CheckBox>(window, "UsageCheck").IsChecked == true;
            s.SubscriptionOnly = Ui.Get<CheckBox>(window, "PassOnlyCheck").IsChecked == true;
            s.DisableThinking = Ui.Get<CheckBox>(window, "ThinkingCheck").IsChecked == true;
            s.Models = models;
            return s;
        }
        private void ModelBusy(bool busy)
        {
            foreach (var name in new[] { "FetchModelsButton", "TestModelButton", "SaveButton" }) Ui.Get<Button>(window, name).IsEnabled = !busy;
        }
        private async Task FetchModels()
        {
            var s = FormSettings();
            var key = Ui.Get<PasswordBox>(window, "KeyBox").Password.Trim();
            if (key.Length == 0) { Error("请先填写 API 密钥。" ); return; }
            var request = new CancellationTokenSource(); modelRequest = request;
            ModelBusy(true); Error("正在读取模型目录…" );
            try
            {
                var identity = ModelCatalog.Identity(s, key);
                var fetched = await ModelCatalog.FetchAsync(s, key, request.Token);
                if (closed) return;
                models = ModelCatalog.Merge(fetched, store.Settings, identity);
                modelsIdentity = identity;
                Ui.Get<ComboBox>(window, "ModelBox").ItemsSource = models;
                Ui.Get<ComboBox>(window, "ModelBox").Text = String.IsNullOrEmpty(s.Model) ? models[0].Id : s.Model;
                Error("已读取 " + models.Count + " 个模型；可测试当前模型后保存。" );
            }
            catch (Exception ex) { if (!closed) Error(ex is InvalidOperationException || ex is ArgumentException ? ex.Message.Replace(key, "[密钥已隐藏]") : "读取列表失败或超时，请检查网络与 API 地址。" ); }
            finally { modelRequest = null; request.Dispose(); if (!closed) ModelBusy(false); }
        }
        private async Task TestModel()
        {
            var s = FormSettings(); s.MaxOutputTokens = 512;
            var key = Ui.Get<PasswordBox>(window, "KeyBox").Password.Trim();
            if (key.Length == 0 || s.Model.Length == 0) { Error("请先填写密钥和模型名称。" ); return; }
            var request = new CancellationTokenSource(); modelRequest = request;
            ModelBusy(true); Error("正在测试当前模型（短翻译）…" );
            try
            {
                var identity = ModelCatalog.Identity(s, key);
                var day = store.BeginRequest();
                var started = DateTime.UtcNow;
                var result = await new ApiClient().TranslateAsync(s, key, "Hello.", "translate", d => { }, request.Token);
                store.FinishRequest(day, result);
                if (closed) return;
                if (modelsIdentity != identity) { models = new List<ModelInfo>(); modelsIdentity = identity; }
                var model = models.FirstOrDefault(x => x.Id == s.Model);
                if (model == null) { model = new ModelInfo { Id = s.Model, Name = ModelCatalog.FriendlyName(s.Model) }; models.Add(model); }
                model.Status = result.Incomplete ? "incomplete" : "verified";
                model.CheckedAt = DateTime.UtcNow.ToString("o");
                model.LatencyMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                Error(result.Incomplete ? "模型可访问，但输出被截断，可提高上限或关闭思考后再试。" : "测试成功 · " + (model.LatencyMs / 1000.0).ToString("0.0") + " 秒 · " + result.Text.Trim());
            }
            catch (Exception ex) { if (!closed) Error(ex is InvalidOperationException || ex is ArgumentException ? ex.Message.Replace(key, "[密钥已隐藏]") : "测试失败或已取消，请检查 API 配置和网络。" ); }
            finally { modelRequest = null; request.Dispose(); if (!closed) ModelBusy(false); }
        }
        private void Save(object sender, RoutedEventArgs args)
        {
            var next = store.Settings.Copy();
            next.BaseUrl = Ui.Get<TextBox>(window, "BaseBox").Text.Trim().TrimEnd('/');
            next.Model = Ui.Get<ComboBox>(window, "ModelBox").Text.Trim();
            next.Protocol = Ui.Get<ComboBox>(window, "ProtocolBox").SelectedIndex == 1 ? "responses" : "chat";
            next.TokenParameter = new[] { "max_completion_tokens", "max_tokens", "none" }[Ui.Get<ComboBox>(window, "TokenBox").SelectedIndex];
            next.IncludeUsage = Ui.Get<CheckBox>(window, "UsageCheck").IsChecked == true;
            next.CacheEnabled = Ui.Get<CheckBox>(window, "CacheCheck").IsChecked == true;
            next.SubscriptionOnly = Ui.Get<CheckBox>(window, "PassOnlyCheck").IsChecked == true;
            next.DisableThinking = Ui.Get<CheckBox>(window, "ThinkingCheck").IsChecked == true;
            int limit;
            if (!Int32.TryParse(Ui.Get<TextBox>(window, "LimitBox").Text.Trim(), out limit) || limit < 512 || limit > 8192)
            { Error("输出上限请输入 512 到 8192 之间的整数。" ); return; }
            next.MaxOutputTokens = limit;
            var key = Ui.Get<PasswordBox>(window, "KeyBox").Password.Trim();
            if (String.IsNullOrWhiteSpace(key) || String.IsNullOrWhiteSpace(next.Model)) { Error("请填写 API 密钥和模型名称。" ); return; }
            if (key.Any(Char.IsWhiteSpace) || next.Model.Any(Char.IsControl)) { Error("密钥或模型名含有无效字符，请检查。" ); return; }
            try { ApiClient.Endpoint(next); }
            catch (ArgumentException ex) { Error(ex.Message); return; }
            try
            {
                string previous = "";
                try { previous = Secrets.Unprotect(next.ProtectedKey); } catch { }
                if (key != previous) next.ProtectedKey = Secrets.Protect(key);
                next.CatalogIdentity = ModelCatalog.Identity(next, key);
                next.Models = next.CatalogIdentity == modelsIdentity ? models : new List<ModelInfo>();
                if (!next.CacheEnabled) store.ClearCache();
                store.SaveSettings(next);
                Saved = true;
                window.DialogResult = true;
            }
            catch { Error("无法保存设置，请检查本机数据目录是否可写。" ); }
        }
        public void Show() { window.ShowDialog(); }
    }

    public sealed class MainController
    {
        public Window Window { get; private set; }
        private readonly LocalStore store;
        private readonly DispatcherTimer debounce;
        private readonly TextBox input;
        private readonly TextBox output;
        private readonly TextBlock status;
        private readonly TextBlock usageLabel;
        private readonly CheckBox autoCheck;
        private readonly CheckBox pinCheck;
        private readonly ComboBox modeBox;
        private readonly ComboBox modelBox;
        private bool bindingModels;
        private CancellationTokenSource catalogRequest;
        private CancellationTokenSource active;
        private int revision;
        private bool closing;
        private bool hotkeyRegistered;
        private int hotkeyError;
        private HwndSource source;
        private string displayedKey;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public MainController(LocalStore storage)
        {
            store = storage;
            Window = Ui.Load("Main");
            input = Ui.Get<TextBox>(Window, "InputBox");
            output = Ui.Get<TextBox>(Window, "ResultBox");
            status = Ui.Get<TextBlock>(Window, "StatusLabel");
            usageLabel = Ui.Get<TextBlock>(Window, "UsageLabel");
            autoCheck = Ui.Get<CheckBox>(Window, "AutoCheck");
            pinCheck = Ui.Get<CheckBox>(Window, "PinCheck");
            modeBox = Ui.Get<ComboBox>(Window, "ModeBox");
            modelBox = Ui.Get<ComboBox>(Window, "MainModelBox");
            modeBox.SelectedIndex = 0;
            autoCheck.IsChecked = store.Settings.AutoTranslate;
            pinCheck.IsChecked = store.Settings.AlwaysOnTop;
            Window.Topmost = store.Settings.AlwaysOnTop;
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            debounce.Tick += async (o, e) => { debounce.Stop(); await Translate(false); };
            input.TextChanged += (o, e) => TextChanged();
            modeBox.SelectionChanged += (o, e) => TextChanged();
            output.TextChanged += (o, e) =>
            {
                Ui.Get<FrameworkElement>(Window, "EmptyState").Visibility = output.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Ui.Get<Button>(Window, "CopyButton").IsEnabled = output.Text.Length > 0;
            };
            Ui.Get<Button>(Window, "TranslateButton").Click += async (o, e) => await Translate(true);
            Ui.Get<Button>(Window, "StopButton").Click += (o, e) => Stop();
            Ui.Get<Button>(Window, "PasteButton").Click += (o, e) => Paste(false);
            Ui.Get<Button>(Window, "ClearButton").Click += (o, e) => { input.Clear(); input.Focus(); };
            Ui.Get<Button>(Window, "CopyButton").Click += (o, e) =>
            {
                try { Clipboard.SetText(output.Text); SetStatus("译文已复制。", false); }
                catch { SetStatus("剪贴板正忙，请稍后再试。", true); }
            };
            Ui.Get<Button>(Window, "SettingsButton").Click += (o, e) => OpenSettings();
            Ui.Get<Button>(Window, "UsageButton").Click += (o, e) => ShowUsage();
            modelBox.SelectionChanged += (o, e) => SwitchModel();
            Ui.Get<Button>(Window, "RefreshModelsButton").Click += async (o, e) => await RefreshModels();
            pinCheck.Click += (o, e) =>
            {
                Window.Topmost = pinCheck.IsChecked == true;
                SavePreferences();
            };
            autoCheck.Click += (o, e) =>
            {
                SavePreferences();
                debounce.Stop();
                if (autoCheck.IsChecked == true && !String.IsNullOrWhiteSpace(input.Text) && store.Settings.Configured) debounce.Start();
                SetStatus(autoCheck.IsChecked == true ? "自动翻译已开启，停止输入 0.8 秒后提交。" : "手动模式 · 按 Ctrl+Enter 翻译。", false);
            };
            Window.PreviewKeyDown += async (o, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await Translate(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; Stop(); }
            };
            Window.SourceInitialized += (o, e) =>
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                source.AddHook(Hook);
                hotkeyRegistered = RegisterHotKey(source.Handle, 4101, 0x4003, 0x54); // Ctrl+Alt+T, no repeat.
                if (!hotkeyRegistered)
                {
                    hotkeyError = Marshal.GetLastWin32Error();
                    Ui.Get<TextBlock>(Window, "HotkeyLabel").Text = hotkeyError == 1409 ? "快捷键已被占用" : "快捷键暂不可用";
                    Ui.Get<TextBlock>(Window, "HotkeyLabel").ToolTip = "可直接点击任务栏中的随译窗口使用。";
                }
            };
            Window.Closing += (o, e) =>
            {
                closing = true;
                CancelCurrent();
                if (catalogRequest != null) catalogRequest.Cancel();
                if (source != null)
                {
                    if (hotkeyRegistered) UnregisterHotKey(source.Handle, 4101);
                    source.RemoveHook(Hook);
                }
            };
            Window.Loaded += (o, e) => input.Focus();
            UpdateLabels();
            FillModels();
            if (!String.IsNullOrEmpty(store.Warning)) SetStatus(store.Warning, true);
            else SetStatus(store.Settings.Configured ? "就绪 · 粘贴内容开始翻译" : "先点击右上角「设置」，连接你的模型 API。", false);
        }
        private void FillModels()
        {
            bindingModels = true;
            try
            {
                var models = store.Settings.Models == null ? new List<ModelInfo>() : store.Settings.Models.ToList();
                if (!String.IsNullOrWhiteSpace(store.Settings.Model) && !models.Any(x => x.Id == store.Settings.Model))
                    models.Insert(0, new ModelInfo { Id = store.Settings.Model, Name = ModelCatalog.FriendlyName(store.Settings.Model), Status = "listed" });
                if (models.Count == 0) models.Add(new ModelInfo { Id = "", Name = "请先设置 API", Status = "listed" });
                modelBox.ItemsSource = models;
                modelBox.SelectedItem = models.FirstOrDefault(x => x.Id == store.Settings.Model) ?? models[0];
                modelBox.IsEnabled = store.Settings.Configured;
                Ui.Get<Button>(Window, "RefreshModelsButton").IsEnabled = store.Settings.Configured && catalogRequest == null;
                var selected = modelBox.SelectedItem as ModelInfo;
                modelBox.ToolTip = selected == null ? "选择翻译模型" : selected.Id + (String.IsNullOrWhiteSpace(selected.CheckedAt) ? "\n尚未实测" : "\n最近验证（UTC）：" + selected.CheckedAt + "\n短请求耗时：" + (selected.LatencyMs / 1000.0).ToString("0.00") + " 秒");
            }
            finally { bindingModels = false; }
        }
        private void SwitchModel()
        {
            if (bindingModels) return;
            var selected = modelBox.SelectedItem as ModelInfo;
            if (selected == null || String.IsNullOrWhiteSpace(selected.Id) || selected.Id == store.Settings.Model) return;
            CancelCurrent();
            try
            {
                var next = store.Settings.Copy(); next.Model = selected.Id;
                store.SaveSettings(next);
                displayedKey = null; output.Clear();
                Ui.Get<Border>(Window, "CacheBadge").Visibility = Visibility.Collapsed;
                UpdateLabels(); FillModels();
                SetStatus("已切换到 " + selected.Name + " · 点击翻译开始。", false);
            }
            catch { FillModels(); SetStatus("模型切换未能保存，请检查数据目录。", true); }
        }
        private async Task RefreshModels()
        {
            if (catalogRequest != null) return;
            var s = store.Settings.Copy();
            string key;
            try { key = Secrets.Unprotect(s.ProtectedKey); }
            catch { SetStatus("无法读取密钥，请重新设置。", true); return; }
            var cts = new CancellationTokenSource(); catalogRequest = cts;
            Ui.Get<Button>(Window, "RefreshModelsButton").IsEnabled = false;
            SetStatus("正在读取模型列表，不发起翻译…", false);
            try
            {
                var identity = ModelCatalog.Identity(s, key);
                var fetched = await ModelCatalog.FetchAsync(s, key, cts.Token);
                if (closing) return;
                var next = store.Settings.Copy();
                if (ModelCatalog.Identity(next, Secrets.Unprotect(next.ProtectedKey)) != identity) return;
                next.Models = ModelCatalog.Merge(fetched, next, identity);
                next.CatalogIdentity = identity;
                store.SaveSettings(next);
                FillModels();
                SetStatus("已更新 " + next.Models.Count + " 个模型 · 列表中的新模型需实际调用验证。", false);
            }
            catch (Exception ex) { if (!closing) SetStatus(ex is InvalidOperationException ? ex.Message : "读取模型列表失败或超时，请检查网络。", true); }
            finally { catalogRequest = null; cts.Dispose(); if (!closing) FillModels(); }
        }
        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == 4101)
            {
                handled = true;
                if (Window.IsEnabled) Paste(true);
            }
            return IntPtr.Zero;
        }
        private void Paste(bool submit)
        {
            try
            {
                if (!Clipboard.ContainsText()) { SetStatus("剪贴板里没有文本。请先复制需要翻译的内容。", false); return; }
                var text = Clipboard.GetText();
                if (text.Length > 6000) { SetStatus("剪贴板文本超过 6000 字符，请分段复制。", true); return; }
                input.Text = text;
                input.CaretIndex = input.Text.Length;
                if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
                Window.Show();
                Window.Activate();
                input.Focus();
                if (submit) { debounce.Stop(); var pending = Translate(true); }
            }
            catch { SetStatus("无法读取剪贴板，请稍后再试。", true); }
        }
        private void SetStatus(string message, bool error)
        {
            status.Text = message;
            status.ToolTip = message;
            status.Foreground = Ui.Color(error ? "#AD5E38" : "#687F85");
        }
        private void Busy(bool busy)
        {
            Ui.Get<Button>(Window, "TranslateButton").IsEnabled = !busy;
            Ui.Get<Button>(Window, "StopButton").IsEnabled = busy;
            Ui.Get<ProgressBar>(Window, "BusyBar").Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        }
        private void CancelCurrent()
        {
            revision++;
            debounce.Stop();
            if (active != null) { active.Cancel(); active = null; }
            Busy(false);
        }
        private void Stop()
        {
            CancelCurrent();
            displayedKey = null;
            SetStatus("已停止接收；已发送的请求仍可能由服务商计费。", false);
        }
        private void TextChanged()
        {
            CancelCurrent();
            displayedKey = null;
            output.Clear();
            Ui.Get<Border>(Window, "CacheBadge").Visibility = Visibility.Collapsed;
            Ui.Get<TextBlock>(Window, "InputHint").Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var count = Ui.Get<TextBlock>(Window, "CountLabel");
            count.Text = input.Text.Length + " / 6000";
            count.Foreground = Ui.Color(input.Text.Length > 6000 ? "#AD5E38" : "#94A2A6");
            if (input.Text.Length > 6000) { SetStatus("原文超过 6000 字符，请分段翻译。", true); return; }
            if (String.IsNullOrWhiteSpace(input.Text)) { SetStatus("就绪 · 粘贴内容开始翻译", false); return; }
            if (!store.Settings.Configured) { SetStatus("先点击「设置」配置 API，再开始翻译。", false); return; }
            if (autoCheck.IsChecked == true) { SetStatus("等待输入停止…", false); debounce.Start(); }
            else SetStatus("按 Ctrl+Enter 或点击「翻译」。", false);
        }
        private string SelectedMode { get { return new[] { "auto", "word", "translate" }[Math.Max(0, modeBox.SelectedIndex)]; } }
        private async Task Translate(bool manual)
        {
            debounce.Stop();
            var original = TranslationRules.Normalize(input.Text);
            if (original.Length == 0) { SetStatus("请先输入或粘贴需要翻译的内容。", false); return; }
            if (input.Text.Length > 6000) { SetStatus("原文超过 6000 字符，请分段翻译。", true); return; }
            if (!store.Settings.Configured)
            {
                if (manual) OpenSettings();
                else SetStatus("请先设置 API。", false);
                return;
            }
            string key, cacheKey;
            var settings = store.Settings.Copy();
            var mode = TranslationRules.DetectMode(original, SelectedMode);
            try { key = Secrets.Unprotect(settings.ProtectedKey); cacheKey = TranslationRules.CacheKey(settings, original, mode); }
            catch { SetStatus("无法读取 API 设置，请重新填写密钥和地址。", true); return; }
            if (!manual && displayedKey == cacheKey) return;
            CancelCurrent();
            Ui.Get<TextBlock>(Window, "ResultHeading").Text = mode == "word" ? "单词释义" : "译文";
            var cached = settings.CacheEnabled ? store.GetCache(cacheKey) : null;
            if (cached != null)
            {
                output.Text = cached;
                output.ScrollToHome();
                displayedKey = cacheKey;
                Ui.Get<Border>(Window, "CacheBadge").Visibility = Visibility.Visible;
                try { store.HitCache(); SetStatus("来自本地缓存 · 本次未调用 API", false); }
                catch { SetStatus("来自本地缓存 · 用量记录保存失败", true); }
                UpdateLabels();
                return;
            }
            output.Clear();
            Ui.Get<Border>(Window, "CacheBadge").Visibility = Visibility.Collapsed;
            string day;
            try { day = store.BeginRequest(); }
            catch { SetStatus("数据目录无法写入，请检查文件夹权限。", true); return; }
            var cts = new CancellationTokenSource();
            active = cts;
            var ownRevision = revision;
            Busy(true);
            UpdateLabels();
            SetStatus("正在连接模型…", false);
            var started = DateTime.UtcNow;
            try
            {
                var result = await new ApiClient().TranslateAsync(settings, key, original, mode, delta =>
                {
                    if (closing || Window.Dispatcher.HasShutdownStarted) return;
                    Window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ownRevision != revision || closing || cts.IsCancellationRequested) return;
                        output.AppendText(delta);
                        SetStatus("正在翻译…", false);
                    }));
                }, cts.Token);
                // Account for a completed request even if the UI has since moved to a new input.
                bool persisted = true;
                try { store.FinishRequest(day, result); } catch { persisted = false; }
                try
                {
                    var next = store.Settings.Copy();
                    if (next.CatalogIdentity == ModelCatalog.Identity(settings, key))
                    {
                        var model = next.Models == null ? null : next.Models.FirstOrDefault(x => x.Id == settings.Model);
                        if (model != null)
                        {
                            model.Status = result.Incomplete ? "incomplete" : "verified";
                            model.CheckedAt = DateTime.UtcNow.ToString("o");
                            model.LatencyMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                            store.SaveSettings(next);
                        }
                    }
                }
                catch { persisted = false; }
                if (ownRevision != revision || closing) return;
                output.Text = result.Text.Trim();
                output.ScrollToHome();
                if (!result.Incomplete)
                {
                    displayedKey = cacheKey;
                    if (settings.CacheEnabled)
                        try { store.PutCache(cacheKey, output.Text); } catch { persisted = false; }
                }
                var elapsed = (DateTime.UtcNow - started).TotalSeconds.ToString("0.0");
                var tokens = result.HasUsage ? " · " + (result.InputTokens + result.OutputTokens).ToString("N0") + " token" : " · 接口未返回用量";
                SetStatus(result.Incomplete ? "输出达到上限，结果可能不完整。请分段翻译或提高输出上限。" : "完成 · " + elapsed + " 秒" + tokens + (persisted ? "" : " · 本地记录保存失败"), result.Incomplete || !persisted);
            }
            catch (OperationCanceledException) { if (ownRevision == revision && !closing) SetStatus("已停止接收。", false); }
            catch (TimeoutException ex) { if (ownRevision == revision && !closing) SetStatus(ex.Message, true); }
            catch (HttpRequestException) { if (ownRevision == revision && !closing) SetStatus("无法连接 API。请检查地址、网络或系统代理。", true); }
            catch (Exception ex)
            {
                if (ownRevision == revision && !closing)
                {
                    var detail = ex is InvalidOperationException ? ex.Message : "请求失败，请检查接口配置后重试。";
                    if (!String.IsNullOrEmpty(key)) detail = detail.Replace(key, "[密钥已隐藏]");
                    SetStatus(detail, true);
                }
            }
            finally
            {
                if (active == cts) { active = null; Busy(false); }
                cts.Dispose();
                if (!closing) UpdateLabels();
            }
        }
        private void SavePreferences()
        {
            try
            {
                var next = store.Settings.Copy();
                next.AutoTranslate = autoCheck.IsChecked == true;
                next.AlwaysOnTop = pinCheck.IsChecked == true;
                store.SaveSettings(next);
            }
            catch { SetStatus("偏好设置未能保存。", true); }
        }
        private void OpenSettings()
        {
            CancelCurrent();
            var dialog = new SettingsDialog(Window, store);
            dialog.Show();
            if (dialog.Saved)
            {
                displayedKey = null;
                Ui.Get<Border>(Window, "CacheBadge").Visibility = Visibility.Collapsed;
                SetStatus("设置已保存 · 点击翻译或按 Ctrl+Enter 开始。", false);
                UpdateLabels();
                FillModels();
            }
        }
        private void UpdateLabels()
        {
            var today = store.Usage.Day(DateTime.Now.ToString("yyyy-MM-dd"));
            usageLabel.Text = "今日 " + today.Requests + " 次 · " + (today.Input + today.Output).ToString("N0") + " token";
            usageLabel.ToolTip = "仅统计接口返回的已知 token；未返回用量的请求请以服务商账单为准。";
            var model = Ui.Get<TextBlock>(Window, "ModelLabel");
            model.Text = store.Settings.Configured ? store.Settings.Model : "尚未配置模型";
            model.ToolTip = store.Settings.Model;
        }
        private void ShowUsage()
        {
            var t = store.Usage.Total;
            var d = store.Usage.Day(DateTime.Now.ToString("yyyy-MM-dd"));
            string body = "今日\nAPI 请求：" + d.Requests + " 次   完成：" + d.Successes + " 次\n已知输入：" + d.Input.ToString("N0") + " token\n已知输出：" + d.Output.ToString("N0") + " token\n缓存命中：" + d.CacheHits + " 次\n\n累计\nAPI 请求：" + t.Requests + " 次\n已知输入：" + t.Input.ToString("N0") + " token\n已知输出：" + t.Output.ToString("N0") + " token\n未取得用量：" + t.Unknown + " 次\n本地缓存：" + store.CacheCount + " 条\n\n中断、失败或接口未返回用量的请求不计入已知 token。\n实际费用以服务商账单为准。";
            MessageBox.Show(Window, body, "随译 · 用量统计", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LingoFloat");
            // Isolated data location for development and portable deployment.
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--data-dir") folder = Path.GetFullPath(args[i + 1]);
            try
            {
                Directory.CreateDirectory(folder);
                string lockName;
                using (var sha = SHA256.Create()) lockName = "Local\\LingoFloat-" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(folder.ToLowerInvariant()))).Replace("-", "").Substring(0, 24);
                bool first;
                using (var mutex = new Mutex(true, lockName, out first))
                {
                    if (!first) { MessageBox.Show("随译已经运行。请使用 Ctrl+Alt+T 呼出已有窗口。", "随译"); return; }
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var controller = new MainController(new LocalStore(folder));
                    app.MainWindow = controller.Window;
                    app.Run(controller.Window);
                }
            }
            catch
            {
                MessageBox.Show("随译无法启动。请确认数据目录可写，并在 Windows 10/11（.NET Framework 4.8）上运行。", "随译", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
