using System.ComponentModel;
using System.Threading.Tasks;
using GlassLingo.Localization;
using GlassLingo.Overlay;

namespace GlassLingo.Sessions
{
    internal sealed class WatchRegion(int id, TranslationSession session, TranslationPresenter presenter) : INotifyPropertyChanged
    {
        internal int Id { get; } = id;
        internal TranslationSession Session { get; } = session;
        internal TranslationPresenter Presenter { get; } = presenter;
        private string _status = "";
        public string Translation { get; private set; } = "";
        public string Name => UiText.F("区域 {0}", Id);
        public string Dimensions => $"{Session.Region.Width} × {Session.Region.Height}";
        public string Status => UiText.T(_status);
        public string PauseLabel => UiText.T(Session.IsPaused ? "恢复" : "暂停");
        public string VisibilityLabel => UiText.T(Presenter.IsHidden ? "显示译文" : "隐藏译文");
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void SetStatus(string value) { _status = value; Refresh(); }
        internal void SetTranslation(string value) { Translation = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Translation))); }
        internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
}
