using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CueATEM;

namespace CueVideo.App;

/// <summary>Small live pictures in the top bar (a port of TopMonitors in ContentView.swift):
/// Live and Next on every page but Operator, which has them large, then the pinned screens,
/// each showing what it's showing now. A picture is a Canon's own or an NDI source; without
/// one, the source's name.</summary>
public sealed class TopMonitors : LiveView
{
    private static Booth B => Booth.Shared;
    private static ATEMState S => B.Atem.State;
    private static ConfigStore Store => B.Store;
    private readonly Pictures.Keeper _pics = new();

    protected override string StructureKey() =>
        $"{B.View}|{B.Me}|{string.Join(",", Store.PinnedScreens.Select(s => s.Id))}";

    public override void Teardown() => _pics.Clear();

    /// <summary>What each pinned screen shows now: one source, or null when its outputs disagree.</summary>
    private static int? Showing(ScreenOutput sc) => RoutingEdit.Now(sc, S).Source;

    protected override UIElement Build()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var pinned = Store.PinnedScreens;
        if (B.View != AppView.Operate)
        {
            row.Children.Add(Monitor(() => S.ProgramOf(B.Me), () => Store.Label(S.ProgramOf(B.Me), S), ("LIVE", T.Program, Brushes.White),
                "What's on Live now. Click to go to the Operator screen.", () => B.View = AppView.Operate));
            row.Children.Add(Monitor(() => S.PreviewOf(B.Me), () => Store.Label(S.PreviewOf(B.Me), S), ("NEXT", T.Preview, T.OnAction),
                "What's on Next. Click to go to the Operator screen.", () => B.View = AppView.Operate));
            if (pinned.Count > 0) row.Children.Add(new Border { Width = 1, Height = 52, Background = T.Line, Margin = new Thickness(4, 0, 12, 0) });
        }
        foreach (var sc in pinned)
        {
            var screen = sc;
            row.Children.Add(Monitor(() => Showing(screen), () => screen.Name, null,
                $"{screen.Name}. Click to open Routing.", () => B.Show(EngineerSection.Routing)));
        }
        Live(() => _pics.Want(row.Children.OfType<Button>().Select(b => b.Tag is Func<int?> f ? f() : null)));
        return row;
    }

    private Button Monitor(Func<int?> input, Func<string> caption, (string Text, Brush Bg, Brush Fg)? tag, string tip, Action go)
    {
        var img = new Image { Stretch = Stretch.UniformToFill };
        var name = T.Text("", 9, FontWeights.SemiBold, T.InkMuted);
        name.TextWrapping = TextWrapping.Wrap; name.TextAlignment = TextAlignment.Center;
        name.HorizontalAlignment = HorizontalAlignment.Center; name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(3, tag == null ? 0 : 12, 3, 0);
        var pic = new Grid { Width = 71, Height = 40, Background = T.SurfaceSunken, ClipToBounds = true };
        pic.Children.Add(name); pic.Children.Add(img);
        if (tag is var (t, bg, fg))
            pic.Children.Add(new Border
            {
                Background = bg, CornerRadius = new CornerRadius(2), Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = T.Text(t, 8, FontWeights.Bold, fg),
            });
        var frame = new Border { Child = pic, BorderBrush = tag?.Bg ?? T.Line, BorderThickness = new Thickness(tag?.Text == "LIVE" ? 2 : 1) };
        var cap = T.Text("", 10, FontWeights.Medium, T.InkMuted);
        cap.Width = 71; cap.TextTrimming = TextTrimming.CharacterEllipsis; cap.TextAlignment = TextAlignment.Center;
        var b = T.Button(T.Column(2, frame, cap), go, T.Transparent, T.Transparent, double.NaN, double.NaN, 4, new Thickness(0), tip);
        b.Margin = new Thickness(0, 0, 8, 0);
        b.Tag = input;
        Live(() =>
        {
            var i = input();
            var p = Pictures.Image(i);
            img.Source = p;
            name.Text = p != null ? "" : i == null ? "Mixed" : Store.RouteLabel(i, S);
            cap.Text = caption();
        });
        return b;
    }
}
