using System.ComponentModel;

namespace WemLabeler;

public class WemEntry : INotifyPropertyChanged
{
    private string? _label;

    public string WemID { get; set; } = "";
    public string Coord { get; set; } = "";
    public string JsonFile { get; set; } = "";
    public string IsStreaming { get; set; } = "";
    public string WemSize { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Path { get; set; } = "";
    public string FoundInBanks { get; set; } = "";
    public string BankCount { get; set; } = "";
    public string Banks { get; set; } = "";

    public string? Label
    {
        get => _label;
        set
        {
            if (_label != value)
            {
                _label = value;
                OnPropertyChanged(nameof(Label));
                OnPropertyChanged(nameof(HasLabel));
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(StatusMark));
            }
        }
    }

    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
    public string StatusMark => HasLabel ? "✓" : "";
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? "" :
        Label.Length <= 40 ? Label : Label[..37] + "...";

    public string SizeDisplay
    {
        get
        {
            if (!long.TryParse(WemSize, out long bytes) || bytes == 0) return "";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / 1048576.0:F1} MB";
        }
    }

    public string CoordSummary => string.IsNullOrEmpty(Coord)
        ? "—" : $"{JsonFile} ({Coord})";

    public string BankSummary
    {
        get
        {
            if (FoundInBanks != "是") return "—";
            return $"✓ {BankCount}个: {Banks}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
