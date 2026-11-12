using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using GlassLingo.Config;
using GlassLingo.Native;
using GlassLingo.Overlay;
using GlassLingo.Sessions;

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static async Task TestLiveInputAndMotionAsync()
        {
            var defaults = new AppConfig();
            Check(defaults.DisplayMode == "popup" && defaults.FullscreenDisplayMode == "sidebar" && defaults.WatchDisplayMode == "inline",
                "default capture is popup, fullscreen is sidebar, and watched regions are inline");
            defaults.FullscreenDisplayMode = "inline"; defaults.UpgradeDisplayDefaults();
            Check(defaults.FullscreenDisplayMode == "sidebar", "existing display defaults migrate once to the requested mode arrangement");
            defaults.FullscreenDisplayMode = "popup"; defaults.UpgradeDisplayDefaults();
            Check(defaults.FullscreenDisplayMode == "popup", "later custom display preferences survive the completed migration");

            var translator = new InputTestTranslator();
            var input = new InputTranslationWindow(new AppConfig(), _ => translator, _ => { }); input.Reveal();
            await Task.Delay(220);
            await SnapshotAsync(input, "input-comfort-empty");
            var box = (TextBox)input.FindName("InputBox");
            box.Text = "H"; await Task.Delay(90); box.Text = "Hello";
            await Task.Delay(300);
            Check(translator.Calls == 0, "typing is debounced instead of sending every keystroke");
            await Task.Delay(500);
            Check(translator.Calls == 1 && input.TranslationText == "你好，世界。", "a short typing pause automatically translates without Enter");
            ((ComboBox)input.FindName("TargetCombo")).SelectedValue = "en-US"; await Task.Delay(750);
            Check(translator.Calls == 2 && translator.LastTarget == "en-US", "changing target automatically translates the existing draft once");
            var composition = new TextComposition(InputManager.Current, box, "こんにちは");
            box.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            box.Text = "こんにちは"; await Task.Delay(550);
            Check(translator.Calls == 2, "IME composition does not submit unfinished text after the debounce interval");
            box.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            await Task.Delay(750);
            Check(translator.Calls == 3 && input.TranslationText.Contains("こんにちは"), "committing IME composition schedules one automatic translation");
            box.Text = "queued"; input.Close(); int calls = translator.Calls; await Task.Delay(550);
            Check(translator.Calls == calls, "closing the input window cancels queued automatic submission");

            var samples = new List<string>(); bool final = false;
            using var flow = new StreamTextMotion((text, done) => { samples.Add(text); final = done; });
            string unicode = "A👩‍💻e\u0301字幕平滑输出测试完成";
            var boundaries = StringInfo.ParseCombiningCharacters(unicode).Append(unicode.Length).ToHashSet();
            var clock = Stopwatch.StartNew(); await flow.FinishAsync(unicode).WaitAsync(TimeSpan.FromSeconds(2));
            Check(samples.All(s => boundaries.Contains(s.Length)) && samples.Last() == unicode && final,
                "stream smoothing never splits emoji or combining characters and delivers the exact final text");
            Check(!flow.Running && clock.ElapsedMilliseconds < 400 && (!SystemParameters.ClientAreaAnimation || samples.Count > 2),
                "stream bursts render intermediate steps with bounded catch-up and no idle timer");
            flow.Push("obsolete streamed content"); flow.Reset(); int paints = samples.Count; await Task.Delay(100);
            Check(samples.Count == paints && !flow.Running, "reset cancels stale stream animation without late painting");

            var inline = new InlineOverlayWindow(new AppConfig());
            inline.Present(new SessionFrame(Layout(("Open gate", 20, 20), ("Follow me", 20, 60)), new Int32Rect(100, 100, 400, 180), "打开城门\n跟", false, false, 90));
            Check(inline.VisualCount == 2, "inline streaming shows the current unfinished row before a newline or completion"); inline.Close();

            using (var presenter = new TranslationPresenter(new AppConfig(), "popup", true))
            {
                var frame = new SessionFrame(Layout(("Open gate", 20, 20)), new Int32Rect(100, 100, 400, 180), "开始生成译文", false, false, 91);
                presenter.Present(frame); presenter.Present(frame with { Text = "完整译文应保留", Final = true });
                presenter.SetMode("sidebar"); await Task.Delay(150);
                var popup = Application.Current.Windows.OfType<ResultOverlayWindow>().Single();
                Check(((TextBlock)popup.FindName("TranslatedText")).Text == "完整译文应保留",
                    "switching display mode during final stream catch-up preserves the complete result");
                presenter.SetMode("sidebar");
                Check(ReferenceEquals(popup, Application.Current.Windows.OfType<ResultOverlayWindow>().Single()),
                    "selecting the active display mode preserves the current window instead of flashing it closed");
            }
            var motionProbe = new Border();
            OverlayMotion.Reveal(motionProbe, true);
            Check(motionProbe.Opacity >= .8, "subtitle entry remains readable on its first frame instead of waiting for a fade from zero");
            OverlayMotion.Settle(motionProbe); await Task.Delay(170);
            Check(motionProbe.Opacity == 1, "interrupting entry leaves content fully visible without a stale animation callback");
            OverlayMotion.Reveal(motionProbe); await Task.Delay(210);
            Check(motionProbe.Opacity == 1, "overlay content settles at full opacity after its short transition");

            var shell = new Shell.ShellWindow((App)Application.Current); shell.Refresh(new AppConfig()); shell.Show();
            var button = (Button)shell.FindName("SettingsButton");
            var tip = new ToolTip { Content = "复制译文 · Ctrl+Shift+C", PlacementTarget = button, Placement = PlacementMode.Bottom };
            tip.IsOpen = true; await Task.Delay(80); tip.ApplyTemplate();
            var content = Find<ContentPresenter>(tip);
            Check(content?.Content as string == "复制译文 · Ctrl+Shift+C" && tip.Background is SolidColorBrush brush && brush.Color.R < 80,
                "tooltips retain visible content on the themed dark background instead of a blank system popup");
            NativeMethods.GetWindowRect(new WindowInteropHelper(shell).Handle, out var bounds);
            using (var shot = Capture.ScreenCapture.CaptureRegion(bounds.Left, bounds.Top, bounds.Width, bounds.Height))
                shot.Save(System.IO.Path.Combine(Output, "tooltip-visible-native.png"));
            tip.IsOpen = false;
            var settings = shell.OpenSettings(new AppConfig(), true);
            var panelSlider = (Slider)settings.FindName("PanelTintSlider"); panelSlider.Value = 75;
            Check(settings.PreviewTint == 1 && !panelSlider.IsEnabled, "transparent glass is an independent opt-in with opaque defaults");
            ((CheckBox)settings.FindName("TransparentGlassCheck")).IsChecked = true;
            Check(Math.Abs(settings.PreviewTint - .25) < .001 && ((TextBlock)settings.FindName("PanelTintValue")).Text.Contains("75"),
                "glass transparency uses a visible percentage while keeping text opacity independent");
            settings.Close(); shell.Hide();
        }
    }
}
