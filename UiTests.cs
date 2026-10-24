using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LingoFloat
{
    public static class UiTests
    {
        private static int checks;
        private static void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            Console.WriteLine("PASS " + label);
            checks++;
        }
        private static void Click(Window w, string name) { Ui.Get<Button>(w, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static async Task Wait(Func<bool> test)
        {
            var time = Stopwatch.StartNew();
            while (!test())
            {
                if (time.ElapsedMilliseconds > 7000) throw new Exception("Timed out waiting for UI");
                await Task.Delay(50);
            }
            await Task.Delay(30);
        }
        private static void Render(Window window, string filename, double width, double height)
        {
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)width * 2, (int)height * 2, 192, 192, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var draw = background.RenderOpen()) draw.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            bmp.Render(background);
            bmp.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var file = File.Create(filename)) encoder.Save(file);
        }
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int exit = 1;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await Run(args[0], args[1]);
                    Console.WriteLine("ALL " + checks + " UI CHECKS PASSED");
                    exit = 0;
                }
                catch (Exception ex) { Console.WriteLine(ex.ToString()); }
                finally { app.Shutdown(); }
            }));
            app.Run();
            return exit;
        }
        private static async Task Run(string endpoint, string folder)
        {
            Directory.CreateDirectory(folder);
            var store = new LocalStore(Path.Combine(folder, "data"));
            var configured = new Settings { BaseUrl = endpoint, Model = "mock-translator", ProtectedKey = Secrets.Protect("local-test-key"), AutoTranslate = false };
            configured.Models.Add(new ModelInfo { Id = "mock-translator", Name = "Mock Translator", Status = "verified" });
            configured.Models.Add(new ModelInfo { Id = "mock-alternative", Name = "Mock Alternative", Status = "listed" });
            configured.CatalogIdentity = ModelCatalog.Identity(configured, "local-test-key");
            store.SaveSettings(configured);
            var controller = new MainController(store);
            var w = controller.Window;
            Render(w, Path.Combine(folder, "01-empty.png"), 444, 651);
            Check(w.Topmost && Ui.Get<CheckBox>(w, "PinCheck").IsChecked == true, "default always-on-top state");
            Check(Ui.Get<FrameworkElement>(w, "ResultBox").ActualHeight > 90, "result panel has readable height");
            Check(!Ui.Get<Button>(w, "CopyButton").IsEnabled, "empty output cannot be copied");
            var input = Ui.Get<TextBox>(w, "InputBox");
            var output = Ui.Get<TextBox>(w, "ResultBox");
            var status = Ui.Get<TextBlock>(w, "StatusLabel");
            input.Text = "learning";
            Click(w, "TranslateButton");
            await Wait(() => status.Text.StartsWith("完成"));
            Check(output.Text.Contains("/ˈlɜːnɪŋ/") && output.Text.Contains("n. 学习"), "word lookup reaches actual result control");
            Check(Ui.Get<TextBlock>(w, "ResultHeading").Text == "单词释义", "word heading selected automatically");
            Check(Ui.Get<Button>(w, "CopyButton").IsEnabled, "completed result can be copied");
            Render(w, Path.Combine(folder, "02-word.png"), 444, 651);
            input.Text = "The model learns from data and improves its predictions.";
            Check(output.Text == "", "editing input clears stale result");
            Click(w, "TranslateButton");
            await Wait(() => status.Text.StartsWith("完成"));
            Check(output.Text == "模型从数据中学习，并逐步改善预测结果。", "sentence translation reaches actual result control");
            Check(store.Usage.Total.Requests == 2, "UI records exactly one request per submit");
            Render(w, Path.Combine(folder, "03-sentence.png"), 444, 651);
            input.Text = "learning";
            Click(w, "TranslateButton");
            Check(status.Text.Contains("本地缓存") && store.Usage.Total.Requests == 2 && store.Usage.Total.CacheHits == 1, "UI cache hit bypasses the network");
            Render(w, Path.Combine(folder, "04-cache.png"), 444, 651);
            Render(w, Path.Combine(folder, "05-small.png"), 384, 591);
            Check(Ui.Get<FrameworkElement>(w, "ResultBox").ActualHeight >= 24, "minimum window retains result viewport");
            Render(w, Path.Combine(folder, "06-large.png"), 620, 820);
            Check(Ui.Get<FrameworkElement>(w, "ResultBox").ActualHeight > 250, "result viewport expands with window");
            var auto = Ui.Get<CheckBox>(w, "AutoCheck");
            auto.IsChecked = true;
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            input.Text = "one";
            await Task.Delay(100);
            input.Text = "two";
            await Task.Delay(100);
            input.Text = "three";
            await Wait(() => status.Text.StartsWith("完成"));
            Check(store.Usage.Total.Requests == 3, "debounce sends only final input");
            auto.IsChecked = false;
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            input.Text = new String('a', 6001);
            Click(w, "TranslateButton");
            Check(status.Text.Contains("超过") && store.Usage.Total.Requests == 3, "oversize input is rejected without charge");
            var slow = store.Settings.Copy(); slow.Model = "slow"; store.SaveSettings(slow);
            input.Text = "stalled request";
            Click(w, "TranslateButton");
            await Task.Delay(150);
            Click(w, "StopButton");
            Check(!Ui.Get<Button>(w, "StopButton").IsEnabled && Ui.Get<Button>(w, "TranslateButton").IsEnabled, "stop restores interactive buttons immediately");
            var normal = store.Settings.Copy(); normal.Model = "mock-translator"; store.SaveSettings(normal);
            input.Text = "Another sentence to translate.";
            Click(w, "TranslateButton");
            await Wait(() => status.Text.StartsWith("完成"));
            var final = output.Text;
            await Task.Delay(600);
            Check(output.Text == final && !String.IsNullOrEmpty(final), "canceled request cannot overwrite new result");
            Check(store.Usage.Total.Unknown == 1, "canceled request is marked as unknown usage");
            var picker = Ui.Get<ComboBox>(w, "MainModelBox");
            var beforeSwitch = store.Usage.Total.Requests;
            picker.SelectedIndex = 1;
            Check(store.Settings.Model == "mock-alternative" && output.Text == "" && store.Usage.Total.Requests == beforeSwitch, "switching model saves selection without making a paid request");
            Check(new LocalStore(store.Folder).Settings.Model == "mock-alternative", "model selection survives restart");
            input.Text = "Check the selected model.";
            Click(w, "TranslateButton");
            await Wait(() => status.Text.StartsWith("完成"));
            Check(output.Text == "备用模型完成翻译。", "selected model is used for the next API request");
            picker.SelectedIndex = 0;
            var beforeRefresh = store.Usage.Total.Requests;
            Click(w, "RefreshModelsButton");
            await Wait(() => status.Text.StartsWith("已更新"));
            Check(picker.Items.Count == 2 && store.Usage.Total.Requests == beforeRefresh, "refreshing model list makes no translation request");
            var settings = new SettingsDialog(null, store);
            var sw = (Window)typeof(SettingsDialog).GetField("window", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(settings);
            Check(Ui.Get<PasswordBox>(sw, "KeyBox").Password == "local-test-key", "settings decrypt key into password field");
            Click(sw, "FetchModelsButton");
            await Wait(() => Ui.Get<TextBlock>(sw, "ErrorLabel").Text.StartsWith("已读取"));
            Check(Ui.Get<ComboBox>(sw, "ModelBox").Items.Count == 2, "settings fetch models with unsaved form credentials");
            Click(sw, "TestModelButton");
            await Wait(() => Ui.Get<TextBlock>(sw, "ErrorLabel").Text.StartsWith("测试成功"));
            Check(store.Usage.Total.Requests == beforeRefresh + 1, "settings model test is counted in usage");
            Ui.Get<TextBox>(sw, "BaseBox").Text = "http://example.com/v1";
            Click(sw, "SaveButton");
            Check(Ui.Get<TextBlock>(sw, "ErrorLabel").Text.Contains("https") && !settings.Saved, "settings validate endpoint before saving");
            Ui.Get<TextBox>(sw, "BaseBox").Text = "https://api.openai.com/v1";
            Ui.Get<ComboBox>(sw, "ModelBox").Text = "your-model-id";
            Ui.Get<PasswordBox>(sw, "KeyBox").Password = "";
            Ui.Get<TextBlock>(sw, "ErrorLabel").Text = "";
            Render(sw, Path.Combine(folder, "07-settings.png"), 484, 701);
            foreach (var child in ((StackPanel)((ScrollViewer)((Grid)sw.Content).Children[1]).Content).Children)
            {
                var expander = child as Expander;
                if (expander != null) expander.IsExpanded = true;
            }
            Render(sw, Path.Combine(folder, "08-settings-expanded.png"), 484, 701);
            Check(Ui.Get<TextBox>(sw, "LimitBox").Text == "4096", "output budget setting loaded");
            Ui.Get<PasswordBox>(sw, "KeyBox").Password = "local-test-key";
            Ui.Get<TextBox>(sw, "BaseBox").Text = endpoint;
            Ui.Get<ComboBox>(sw, "ModelBox").Text = "mock-translator";
            sw.Loaded += (o, e) => sw.Dispatcher.BeginInvoke(new Action(() => Click(sw, "SaveButton")));
            sw.ShowDialog();
            Check(settings.Saved && store.Settings.Model == "mock-translator", "settings save and modal close complete successfully");
            status.Text = new String('测', 240);
            Render(w, Path.Combine(folder, "09-small-error.png"), 384, 591);
            Check(output.ActualHeight >= 24, "long error message preserves minimum result viewport");
            new WindowInteropHelper(w).EnsureHandle();
            var registered = (bool)typeof(MainController).GetField("hotkeyRegistered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
            var hotkeyError = (int)typeof(MainController).GetField("hotkeyError", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
            Console.WriteLine("Native hotkey registered=" + registered + " WindowsError=" + hotkeyError);
            Check(registered || (hotkeyError != 0 && Ui.Get<TextBlock>(w, "HotkeyLabel").Text.Contains("快捷键")), "native hotkey registration or visible unavailable fallback");
            sw.Close();
            w.Close();
        }
    }
}
