using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using JTW.NetworkScanner.Core;

namespace JTW.NetworkScanner.Modules;

/// <summary>
/// Base for every tool window. Modules are modeless (the main window stays usable); closing a module
/// while it's running just hides it so the job keeps going — it can be brought back from the
/// "Running modules" list in the main window.
/// </summary>
public class ModuleWindowBase : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        protected set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnChanged(nameof(IsRunning));
            OnChanged(nameof(StateText));
        }
    }

    private string _summary = "";
    /// <summary>Short live status shown in the main window's module list.</summary>
    public string Summary
    {
        get => _summary;
        protected set { _summary = value; OnChanged(nameof(Summary)); }
    }

    public string ModuleName { get; protected set; } = "Module";

    public string StateText => IsRunning ? "● Running" : "Idle";

    private bool _forceClose;

    public ModuleWindowBase()
    {
        SetResourceReference(StyleProperty, "JtwWindow");
        Width = 900; Height = 620;
        MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        ModuleManager.Register(this);
    }

    protected void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsRunning && !_forceClose)
        {
            // Keep the diagnostic alive in the background.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopModule();
        ModuleManager.Unregister(this);
        base.OnClosed(e);
    }

    /// <summary>Override to cancel any running work.</summary>
    public virtual void StopModule() { }

    public void ForceClose()
    {
        _forceClose = true;
        StopModule();
        Close();
    }

    public void Retrieve()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected static void ShowError(string message) =>
        MessageBox.Show(message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
}

public static class ModuleManager
{
    public static ObservableCollection<ModuleWindowBase> Modules { get; } = new();

    internal static void Register(ModuleWindowBase w)
    {
        if (!Modules.Contains(w)) Modules.Add(w);
    }

    internal static void Unregister(ModuleWindowBase w) => Modules.Remove(w);

    public static T Open<T>(Window owner, Action<T> configure = null) where T : ModuleWindowBase, new()
    {
        var w = new T();
        if (owner != null && owner.IsVisible) { w.Left = owner.Left + 60; w.Top = owner.Top + 60; w.WindowStartupLocation = WindowStartupLocation.Manual; }
        configure?.Invoke(w);
        w.Show();
        return w;
    }

    public static void CloseAll()
    {
        foreach (var m in Modules.ToList()) m.ForceClose();
    }
}
