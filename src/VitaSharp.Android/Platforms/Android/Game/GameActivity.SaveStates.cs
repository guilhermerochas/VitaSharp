using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AView = Android.Views.View;
using Color = Android.Graphics.Color;
using ScrollView = Android.Widget.ScrollView;

namespace VitaSharp.Android.Game;

/// <summary>The save state slots of the pause menu: a picture and the date of each slot; saving or loading runs off the
/// UI thread and ends with a toast.</summary>
public sealed partial class GameActivity
{
    void ShowStateSlots(bool save) => new Handler(Looper.MainLooper!).Post(() =>
    {
        if (_session is not { } session) return;
        _menuOpen = true;
        session.Pause();
        var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        AlertDialog? dialog = null;
        for (int slot = 1; slot <= EmulationSession.StateSlots; slot++)
        {
            int s = slot;
            var info = session.StateInfo(s);
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetPadding(24, 12, 24, 12);
            var image = new ImageView(this);
            image.SetMinimumWidth(192); image.SetMinimumHeight(108);
            if (info?.Thumbnail is { Length: > 0 } png && BitmapFactory.DecodeByteArray(png, 0, png.Length) is { } bmp)
                image.SetImageBitmap(Bitmap.CreateScaledBitmap(bmp, 192, 108, true));
            else image.SetBackgroundColor(Color.DarkGray);
            row.AddView(image, new LinearLayout.LayoutParams(192, 108));
            var text = new TextView(this) { Text = info != null ? $"Slot {s}\n{info.CreatedUtc.ToLocalTime():g}" : $"Slot {s}\nvazio" };
            text.SetPadding(24, 0, 0, 0);
            row.AddView(text);
            row.Enabled = save || info != null;
            row.Clickable = true;
            row.Click += (_, _) =>
            {
                dialog?.Dismiss();
                Toast.MakeText(this, save ? "Salvando estado…" : "Carregando estado…", ToastLength.Short)?.Show();
                Task.Run(() =>
                {
                    string msg = save ? session.SaveState(s) : session.LoadState(s);
                    RunOnUiThread(() => Toast.MakeText(this, msg, ToastLength.Long)?.Show());
                });
            };
            list.AddView(row);
        }
        var scroll = new ScrollView(this);
        scroll.AddView(list);
        dialog = new AlertDialog.Builder(this)
            .SetTitle(save ? "Salvar estado" : "Carregar estado")!
            .SetView(scroll)!
            .SetNegativeButton("Voltar", (_, _) => { })!
            .SetOnDismissListener(new DismissListener(() =>
            {
                _menuOpen = false;
                if (!IsFinishing) session.Resume();
                EnterImmersive();
            }))!
            .Create();
        dialog!.Show();
    });
}
