using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlassLingo.Localization;

namespace GlassLingo.Hotkey
{
    public sealed class ShortcutCaptureBox : TextBox
    {
        private string _original = "";
        private string? _pending;
        private Key _pendingKey;
        private static readonly DependencyPropertyKey RecordingKey = DependencyProperty.RegisterReadOnly(nameof(IsRecording), typeof(bool), typeof(ShortcutCaptureBox), new PropertyMetadata(false));
        public static readonly DependencyProperty IsRecordingProperty = RecordingKey.DependencyProperty;
        public bool IsRecording => (bool)GetValue(IsRecordingProperty);
        internal event Action<bool>? RecordingChanged;
        internal event Action<string, bool?>? Feedback;
        internal Func<string, string?>? ValidateCandidate;
        public ShortcutCaptureBox()
        {
            IsReadOnly = true; IsReadOnlyCaretVisible = false;
            InputMethod.SetIsInputMethodEnabled(this, false);
            IsUndoEnabled = false; Cursor = Cursors.Hand;
            Unloaded += (_, _) => CancelRecording(false);
        }
        internal void BeginRecording()
        {
            if (IsRecording) return;
            _original = Text; _pending = null;
            SetValue(RecordingKey, true); UiText.Changed += RefreshPrompt;
            Text = UiText.T("按下组合键…");
            RecordingChanged?.Invoke(true);
            Feedback?.Invoke(UiText.T("点击后按下组合键；Esc 取消，Tab 切换下一项。"), null);
        }
        private void EndRecording()
        {
            SetValue(RecordingKey, false); _pending = null;
            UiText.Changed -= RefreshPrompt; RecordingChanged?.Invoke(false);
        }
        private void RefreshPrompt() { if (IsRecording && _pending == null) Text = UiText.T("按下组合键…"); }
        internal void CancelRecording(bool notify = true)
        {
            if (!IsRecording) return;
            Text = _original; EndRecording();
            if (notify) Feedback?.Invoke(UiText.T("快捷键录入已取消。"), null);
        }
        private static Key ActualKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        internal bool HandleDown(Key key, ModifierKeys modifiers, bool repeat = false)
        {
            if (key == Key.Tab && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            { CancelRecording(false); return false; }
            if (key == Key.Escape)
            { if (!IsRecording) return false; CancelRecording(); return true; }
            if (!IsRecording) BeginRecording();
            if (repeat) return true;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
            {
                _pending = null;
                Text = ModifierLabel(modifiers) + "…";
                return true;
            }
            if (!ShortcutParser.TryFromKey(key, modifiers, out string value))
            { _pending = null; Text = UiText.T("按下组合键…"); Feedback?.Invoke(UiText.T("请同时按住 Ctrl 或 Alt，再按一个键。"), false); return true; }
            string? error = ValidateCandidate?.Invoke(value);
            if (error != null)
            { _pending = null; Text = value; Feedback?.Invoke(error, false); return true; }
            _pending = value; _pendingKey = key; Text = value;
            return true;
        }
        internal void HandleUp(Key key)
        {
            if (!IsRecording || _pending == null || key != _pendingKey) return;
            string value = _pending;
            _original = Text = value;
            EndRecording();
            Feedback?.Invoke(UiText.T("快捷键已录入，保存后生效。"), true);
        }
        private static string ModifierLabel(ModifierKeys mods)
            => ((mods & ModifierKeys.Control) != 0 ? "Ctrl+" : "") + ((mods & ModifierKeys.Alt) != 0 ? "Alt+" : "") + ((mods & ModifierKeys.Shift) != 0 ? "Shift+" : "");
        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); BeginRecording(); }
        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { CancelRecording(false); base.OnLostKeyboardFocus(e); }
        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        { if (IsKeyboardFocused) BeginRecording(); base.OnPreviewMouseLeftButtonDown(e); }
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!e.Handled && HandleDown(ActualKey(e), Keyboard.Modifiers, e.IsRepeat)) e.Handled = true;
            if (!e.Handled) base.OnPreviewKeyDown(e);
        }
        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if (IsRecording) { HandleUp(ActualKey(e)); e.Handled = true; }
            if (!e.Handled) base.OnPreviewKeyUp(e);
        }
    }
}
