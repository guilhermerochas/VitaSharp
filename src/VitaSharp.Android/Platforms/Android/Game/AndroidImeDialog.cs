using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Game;

/// <summary>
/// Built into the app, not run on a device yet. The host text entry on Android, for the system IME dialog
/// (<see cref="IImeDialogBackend"/>, sceImeDialog) and the in-game keyboard (<see cref="ITextInputBackend"/>, SceIme):
/// a dialog over the game with the guest's title and an EditText limited to the guest's maximum length and input type
/// (number, URL, mail, password), which raises the system soft keyboard (InputMethodManager). OK, the keyboard's
/// Done key or Enter confirm; Cancel, Back or a controller's B cancel. A physical controller moves between the field and
/// the buttons and presses them (the dialog's own key handling); a hardware keyboard types into the field.
/// Every call is marshalled to the UI thread.
/// </summary>
public sealed class AndroidImeDialog(Activity activity) : IImeDialogBackend, ITextInputBackend
{
    AlertDialog? _dialog;
    EditText? _edit;
    int _session;
    bool _suppressEcho;

    // ---------------------------------------------------------------- IImeDialogBackend

    public void Open(ImeDialogRequest r, Action<string?> done) => activity.RunOnUiThread(() =>
        Show(r.Title, r.InitialText, r.MaxLength, r.Type, r.Multiline, r.Password, done, null, null, null));

    public void Close() => activity.RunOnUiThread(Dismiss);

    // ---------------------------------------------------------------- ITextInputBackend (SceIme)

    public void Begin(string initial, int maxLength, int type, bool multiline, Action<string, int> onTextChanged, Action onEnter, Action onClose) =>
        activity.RunOnUiThread(() => Show("", initial, maxLength, type, multiline, false, null, onTextChanged, onEnter, onClose));

    public void SetText(string text, int caret) => activity.RunOnUiThread(() =>
    {
        if (_edit == null) return;
        _suppressEcho = true; // the change came from the game: not reported back
        _edit.Text = text;
        _edit.SetSelection(Math.Clamp(caret, 0, _edit.Text?.Length ?? 0));
        _suppressEcho = false;
    });

    public void End() => activity.RunOnUiThread(Dismiss);

    // ---------------------------------------------------------------- UI thread

    /// <summary>SceImeType to an Android input type: 1 basic Latin (visible-password keyboards are ASCII), 2 URL,
    /// 3 mail, 4 number, 5 extended number (decimal, signed); password text boxes hide the characters.</summary>
    static InputTypes InputTypeFor(int type, bool multiline, bool password)
    {
        if (type is 4 or 5)
        {
            var n = InputTypes.ClassNumber;
            if (type == 5) n |= InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned;
            if (password) n |= InputTypes.NumberVariationPassword;
            return n;
        }
        var t = InputTypes.ClassText;
        t |= password ? InputTypes.TextVariationPassword : type switch
        {
            1 => InputTypes.TextVariationVisiblePassword,
            2 => InputTypes.TextVariationUri,
            3 => InputTypes.TextVariationEmailAddress,
            _ => InputTypes.TextFlagNoSuggestions,
        };
        if (multiline) t |= InputTypes.TextFlagMultiLine;
        return t;
    }

    void Show(string title, string initial, int maxLength, int type, bool multiline, bool password,
              Action<string?>? done, Action<string, int>? changed, Action? enter, Action? close)
    {
        Dismiss();
        int session = ++_session;
        bool answered = false;
        var edit = new EditText(activity) { Text = initial };
        edit.SetFilters([new InputFilterLengthFilter(Math.Max(1, maxLength))]);
        edit.InputType = InputTypeFor(type, multiline, password);
        edit.ImeOptions = multiline ? ImeAction.Unspecified : ImeAction.Done;
        if (!multiline) edit.SetSingleLine(true);
        edit.SetSelection(edit.Text?.Length ?? 0);

        void Answer(bool ok)
        {
            if (answered || session != _session) return;
            string text = edit.Text ?? "";
            Log.Info($"IME (Android): {(ok ? $"entered \"{(password ? new string('*', text.Length) : text)}\"" : "canceled")}");
            if (done != null) { answered = true; Dismiss(); done(ok ? text : null); }
            else if (ok) enter?.Invoke();  // SceIme: Enter can come again; the game closes the keyboard (sceImeClose -> End)
            else { answered = true; Dismiss(); close?.Invoke(); }
        }

        if (changed != null)
            edit.TextChanged += (_, _) =>
            {
                if (!_suppressEcho && session == _session) changed(edit.Text ?? "", edit.SelectionEnd);
            };
        edit.EditorAction += (_, e) =>
        {
            bool enterKey = e.Event is { KeyCode: Keycode.Enter, Action: KeyEventActions.Down };
            e.Handled = e.ActionId == ImeAction.Done || (enterKey && !multiline);
            if (e.Handled) Answer(true);
        };

        var container = new FrameLayout(activity);
        int pad = (int)(20 * (activity.Resources?.DisplayMetrics?.Density ?? 2f));
        container.SetPadding(pad, pad / 2, pad, 0);
        container.AddView(edit);
        var dialog = new AlertDialog.Builder(activity)
            .SetTitle(title.Length > 0 ? title : "Digite o texto")!
            .SetView(container)!
            .SetPositiveButton("OK", (_, _) => Answer(true))!
            .SetNegativeButton("Cancelar", (_, _) => Answer(false))!
            .SetOnCancelListener(new CancelListener(() => Answer(false)))!
            .Create()!;
        // the soft keyboard comes up with the dialog
        dialog.Window?.SetSoftInputMode(SoftInput.StateAlwaysVisible | SoftInput.AdjustResize);
        _dialog = dialog;
        _edit = edit;
        dialog.Show();
        edit.RequestFocus();
        var imm = activity.GetSystemService(Context.InputMethodService) as InputMethodManager;
        edit.Post(() => imm?.ShowSoftInput(edit, ShowFlags.Implicit));
    }

    void Dismiss()
    {
        _session++;
        var d = _dialog;
        if (_edit is { } e && activity.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
            imm.HideSoftInputFromWindow(e.WindowToken, HideSoftInputFlags.None);
        _dialog = null;
        _edit = null;
        if (d?.IsShowing == true) d.Dismiss();
    }

    sealed class CancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => onCancel();
    }
}
