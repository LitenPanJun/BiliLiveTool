using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using BiliLiveTool.UI.ViewModels;

namespace BiliLiveTool.UI.Views;

/// <summary>控制台页视图：日志尾随滚动（新增即跟随到底部）。</summary>
public partial class ConsoleView : UserControl
{
    private NotifyCollectionChangedEventHandler? _tailHandler;

    public ConsoleView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is ConsoleViewModel vm && this.FindControl<ListBox>("LogList") is { } list)
        {
            _tailHandler = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && vm.Lines.Count > 0)
                    list.ScrollIntoView(vm.Lines[^1]);
            };
            vm.Lines.CollectionChanged += _tailHandler;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_tailHandler is not null && DataContext is ConsoleViewModel vm)
            vm.Lines.CollectionChanged -= _tailHandler;
        _tailHandler = null;

        base.OnDetachedFromVisualTree(e);
    }
}
