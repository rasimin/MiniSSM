using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace SSMS;

public class ExplorerNode : INotifyPropertyChanged
{
    private string _name = "";
    private string _kind = "";
    private string _database = "";
    private string _objectName = "";
    private string _connectionString = "";
    private bool _isLoaded;
    private ContextMenu? _nodeContextMenu;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Kind
    {
        get => _kind;
        set => SetProperty(ref _kind, value);
    }

    public string Database
    {
        get => _database;
        set => SetProperty(ref _database, value);
    }

    public string ObjectName
    {
        get => _objectName;
        set => SetProperty(ref _objectName, value);
    }

    public string ConnectionString
    {
        get => _connectionString;
        set => SetProperty(ref _connectionString, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        set => SetProperty(ref _isLoaded, value);
    }

    public bool IsPrimaryKey { get; set; }
    public string DataType { get; set; } = "";

    public ContextMenu? NodeContextMenu
    {
        get => _nodeContextMenu;
        set => SetProperty(ref _nodeContextMenu, value);
    }

    public ObservableCollection<ExplorerNode> Children { get; } = [];

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
