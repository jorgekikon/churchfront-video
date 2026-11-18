using System.Windows;
using System.Windows.Controls;

namespace CueVideo.App;

/// <summary>Picks an NDI source from those on the network (or none), as the Mac's NDIPicker.
/// Lists what's already known at once, and looks again each time it opens.</summary>
public static class NdiPicker
{
    /// <summary>A button showing the choice now; clicking it opens the list.</summary>
    public static Button Make(string? selection, string none, Action<string?> set, double w = 280, string? tip = null)
    {
        var dock = new DockPanel { LastChildFill = true };
        var arrow = T.Text("▾", 11, null, T.InkMuted);
        DockPanel.SetDock(arrow, Dock.Right);
        arrow.Margin = new Thickness(6, 0, 0, 0);
        dock.Children.Add(arrow);
        var text = T.Text(string.IsNullOrEmpty(selection) ? none : Ndi.ShortName(selection), 13, FontWeights.SemiBold, string.IsNullOrEmpty(selection) ? T.InkMuted : T.Ink);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        dock.Children.Add(T.Row(6, T.Text("📡", 11), text));
        var b = T.Button(dock, null, T.SurfaceRaised, T.Line, w, 30, 6, new Thickness(10, 0, 8, 0), tip ?? selection);
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.Click += (_, _) => Open(b, selection, none, set);
        return b;
    }

    /// <summary>The list, under `target`.</summary>
    public static void Open(FrameworkElement target, string? selection, string none, Action<string?> set)
    {
        var m = T.Menu();
        void Fill()
        {
            m.Items.Clear();
            m.Items.Add(T.Item(none, () => set(null)));
            m.Items.Add(new Separator());
            if (!NdiDirectory.HasRuntime) m.Items.Add(new MenuItem { Header = "NDI isn't installed on this computer (install NDI Tools)", IsEnabled = false, Foreground = T.InkMuted });
            else if (NdiDirectory.Sources.Count == 0) m.Items.Add(new MenuItem { Header = NdiDirectory.Scanning ? "Looking for NDI sources…" : "No NDI sources found yet", IsEnabled = false, Foreground = T.InkMuted });
            foreach (var n in NdiDirectory.Sources)
            {
                var name = n;
                m.Items.Add(T.Item(Ndi.ShortName(n) + (n == selection ? "  ✓" : ""), () => set(name)));
            }
            // A saved source that isn't on the network now still shows, so it can be seen and cleared.
            if (!string.IsNullOrEmpty(selection) && !NdiDirectory.Sources.Contains(selection))
                m.Items.Add(new MenuItem { Header = Ndi.ShortName(selection) + "  ✓ (not on the network now)", IsEnabled = false, Foreground = T.InkMuted });
            m.Items.Add(new Separator());
            m.Items.Add(T.Item("Look again", () => { NdiDirectory.Scan(); }));
        }
        Fill();
        // Look again in the background; the open list fills in when the answer comes.
        NdiDirectory.Scan(() => { if (m.IsOpen) Fill(); });
        m.PlacementTarget = target;
        m.IsOpen = true;
    }
}
