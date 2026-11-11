using VitaSharp.Core.Localization;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using VitaSharp.Core.Util;
using AView = Android.Views.View;
using Color = Android.Graphics.Color;
using ScrollView = Android.Widget.ScrollView;

namespace VitaSharp.Android.Game;

/// <summary>Scripted save state steps; the slot pages of the pause menu are in GameActivity.PauseMenu.cs.</summary>
public sealed partial class GameActivity
{
    /// <summary>A scripted <c>savestate</c>/<c>loadstate</c> step (VITASHARP_ANDROID_INPUT[_LIVE]): what picking the slot in
    /// the pause menu does (menu picture, pause, save or load in the background, resume when the dialog closes).</summary>
    void ScriptedState(bool save, int slot)
    {
        if (_session is not { } session || slot < 1 || slot > EmulationSession.StateSlots)
            return;
        if (save)
            session.CaptureMenuThumbnail();
        session.Pause();
        Toast.MakeText(this, save ? Loc.Get("MSavingState") : Loc.Get("MLoadingState"), ToastLength.Short)?.Show();
        Task.Run(() =>
        {
            string msg = save ? session.SaveState(slot) : session.LoadState(slot);
            Log.Info($"Scripted {(save ? "save" : "load")} state, slot {slot}: {msg}");
            RunOnUiThread(() => Toast.MakeText(this, msg, ToastLength.Long)?.Show());
        });
        if (!IsFinishing)
            session.Resume();
    }
}
