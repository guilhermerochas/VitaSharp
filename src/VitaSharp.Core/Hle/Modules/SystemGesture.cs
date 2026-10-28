using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSystemGesture: turns raw SceTouchData (supplied by the game every frame) into primitive touch events and
/// recognised gestures (tap, drag, tap-and-hold, pinch). Coordinates are touch-panel units (front 1920x1088).
/// </summary>
public sealed class SystemGesture : HleModule
{
    const int ERR_INVALID_ARG = unchecked((int)0x80800001), ERR_NOT_INITIALIZED = unchecked((int)0x80800002);
    // SceSystemGestureType: one bit per recognizer kind; 0x10 = rotation (two-finger twist). PhyreEngine games
    // create one of each and abort their whole init if any create fails.
    const uint TYPE_TAP = 1, TYPE_DRAG = 2, TYPE_TAP_AND_HOLD = 4, TYPE_PINCH = 8, TYPE_ROTATION = 0x10;
    const uint STATE_INACTIVE = 0, STATE_BEGIN = 1, STATE_ACTIVE = 2, STATE_END = 3;

    const int TapMaxMove = 40;                               // panel units
    const ulong TapMaxTime = 500_000, HoldTime = 500_000;  // Âµs
    const ulong MultiTapWindow = 350_000;

    sealed class Primitive
    {
        public ushort Id;
        public int Port;
        public byte ReportId;
        public uint State;
        public short PressX, PressY, PressForce, CurX, CurY, CurForce, DX, DY, DForce;
        public ulong PressTime, LastTime, DeltaTime;
        public bool Seen;
    }

    readonly object _lock = new();
    bool _initialized;
    ushort _nextPrimitive = 1;
    readonly List<Primitive> _primitives = new();     // live + just ended
    ulong _now;

    sealed class Recognizer
    {
        public uint Type;
        public int Port;
        public short RX, RY, RW, RH;
        public int MaxTapCount = 1;
        public uint NextEventId = 1;
        public ulong UpdatedTime;
        public readonly List<byte[]> Events = new();
        // per-primitive gesture tracking
        public readonly Dictionary<ushort, (uint State, ulong Begin, uint EventId)> Tracks = new();
        public int TapCount;
        public ulong LastTapTime;
        public short LastTapX, LastTapY;
        public uint PinchEventId;
        public float PinchStartDist;
        public bool PinchActive;
        public float RotationStartAngle;
    }

    readonly Dictionary<uint, Recognizer> _recognizers = new();

    // ------------------------------------------------------------------ primitive recognizer

    [Hle]
    int sceSystemGestureInitializePrimitiveTouchRecognizer(uint param)
    {
        lock (_lock) { _initialized = true; _primitives.Clear(); }
        return 0;
    }

    [Hle] int sceSystemGestureFinalizePrimitiveTouchRecognizer() { lock (_lock) { _initialized = false; _primitives.Clear(); } return 0; }
    [Hle] int sceSystemGestureResetPrimitiveTouchRecognizer() { lock (_lock) _primitives.Clear(); return 0; }

    [Hle]
    int sceSystemGestureUpdatePrimitiveTouchRecognizer(uint front, uint back)
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            _primitives.RemoveAll(p => p.State == STATE_END);
            foreach (var p in _primitives) p.Seen = false;
            ulong stamp = Emu.Kernel.ProcessTimeUs;
            if (front != 0) stamp = Math.Max(stamp, GuestMemory.Read<ulong>(front));
            _now = stamp;
            Consume(front, 0);
            Consume(back, 1);
            foreach (var p in _primitives)
            {
                if (p.Seen) continue;
                p.State = STATE_END;
                p.DX = 0; p.DY = 0; p.DForce = 0;
            }
        }
        return 0;
    }

    void Consume(uint data, int port)
    {
        if (data == 0) return;
        uint count = Math.Min(GuestMemory.R32(data + 12), 8u);
        for (uint i = 0; i < count; i++)
        {
            uint r = data + 16 + i * 16;
            byte id = GuestMemory.Read<byte>(r);
            short force = GuestMemory.Read<byte>(r + 1);
            short x = (short)GuestMemory.Read<ushort>(r + 2), y = (short)GuestMemory.Read<ushort>(r + 4);
            var p = _primitives.FirstOrDefault(q => q.Port == port && q.ReportId == id && q.State != STATE_END);
            if (p == null)
            {
                p = new Primitive
                {
                    Id = _nextPrimitive++, Port = port, ReportId = id, State = STATE_BEGIN,
                    PressX = x, PressY = y, PressForce = force, CurX = x, CurY = y, CurForce = force, PressTime = _now, LastTime = _now,
                };
                if (_nextPrimitive == 0) _nextPrimitive = 1;
                _primitives.Add(p);
            }
            else
            {
                p.State = STATE_ACTIVE;
                p.DX = (short)(x - p.CurX); p.DY = (short)(y - p.CurY); p.DForce = (short)(force - p.CurForce);
                p.DeltaTime = _now - p.LastTime;
                p.CurX = x; p.CurY = y; p.CurForce = force; p.LastTime = _now;
            }
            p.Seen = true;
        }
    }

    void WritePrimitive(uint dst, Primitive p)
    {
        GuestMemory.Fill(dst, 0, 96);
        GuestMemory.W32(dst, p.State);
        GuestMemory.Write(dst + 4, p.Id);
        GuestMemory.Write(dst + 6, p.PressX); GuestMemory.Write(dst + 8, p.PressY);
        GuestMemory.Write(dst + 10, p.PressForce);
        GuestMemory.Write(dst + 12, p.CurX); GuestMemory.Write(dst + 14, p.CurY);
        GuestMemory.Write(dst + 16, p.CurForce);
        GuestMemory.Write(dst + 18, p.DX); GuestMemory.Write(dst + 20, p.DY);
        GuestMemory.Write(dst + 22, p.DForce);
        GuestMemory.Write(dst + 24, p.DeltaTime);
        GuestMemory.Write(dst + 32, _now - p.PressTime);
    }

    [Hle]
    int sceSystemGestureGetPrimitiveTouchEvents(uint buffer, uint capacity, Ptr<uint> num)
    {
        lock (_lock)
        {
            uint n = 0;
            foreach (var p in _primitives)
            {
                if (n >= capacity) break;
                if (buffer != 0) WritePrimitive(buffer + n * 96, p);
                n++;
            }
            num.TrySet(n);
        }
        return 0;
    }

    [Hle] int sceSystemGestureGetPrimitiveTouchEventsCount() { lock (_lock) return _primitives.Count; }

    [Hle]
    int sceSystemGestureGetPrimitiveTouchEventByIndex(uint index, uint dst)
    {
        lock (_lock)
        {
            if (index >= _primitives.Count || dst == 0) return ERR_INVALID_ARG;
            WritePrimitive(dst, _primitives[(int)index]);
        }
        return 0;
    }

    [Hle]
    int sceSystemGestureGetPrimitiveTouchEventByPrimitiveID(uint id, uint dst)
    {
        lock (_lock)
        {
            var p = _primitives.FirstOrDefault(q => q.Id == id);
            if (p == null || dst == 0) return ERR_INVALID_ARG;
            WritePrimitive(dst, p);
        }
        return 0;
    }

    // ------------------------------------------------------------------ touch recognizers

    static (short X, short Y, short W, short H) ReadRect(uint rect) =>
        rect == 0 ? ((short)0, (short)0, (short)1920, (short)1088)
                  : (GuestMemory.Read<short>(rect), GuestMemory.Read<short>(rect + 2), GuestMemory.Read<short>(rect + 4), GuestMemory.Read<short>(rect + 6));

    [Hle]
    int sceSystemGestureCreateTouchRecognizer(uint rec, uint type, uint port, uint rect, uint param)
    {
        if (rec == 0 || type is not (TYPE_TAP or TYPE_DRAG or TYPE_TAP_AND_HOLD or TYPE_PINCH or TYPE_ROTATION)) return ERR_INVALID_ARG;
        var (x, y, w, h) = ReadRect(rect);
        var r = new Recognizer { Type = type, Port = (int)(port & 0xFF), RX = x, RY = y, RW = w, RH = h };
        if (type == TYPE_TAP && param != 0) r.MaxTapCount = Math.Max(1, (int)GuestMemory.Read<byte>(param));
        GuestMemory.Fill(rec, 0, 307 * 8);
        lock (_lock) _recognizers[rec] = r;
        Util.Log.Debug($"gesture recognizer 0x{rec:X8} type {type} port {port} rect ({x},{y},{w},{h})");
        return 0;
    }

    [Hle]
    int sceSystemGestureUpdateTouchRecognizerRectangle(uint rec, uint rect)
    {
        lock (_lock)
        {
            if (!_recognizers.TryGetValue(rec, out var r)) return ERR_INVALID_ARG;
            (r.RX, r.RY, r.RW, r.RH) = ReadRect(rect);
        }
        return 0;
    }

    [Hle]
    int sceSystemGestureResetTouchRecognizer(uint rec)
    {
        lock (_lock)
        {
            if (!_recognizers.TryGetValue(rec, out var r)) return ERR_INVALID_ARG;
            r.Events.Clear();
            r.Tracks.Clear();
            r.TapCount = 0;
            r.PinchActive = false;
        }
        return 0;
    }

    bool Inside(Recognizer r, short x, short y) => x >= r.RX && y >= r.RY && x < r.RX + r.RW && y < r.RY + r.RH;

    byte[] NewEvent(Recognizer r, uint id, uint state)
    {
        var e = new byte[144];
        BitConverter.TryWriteBytes(e.AsSpan(0), id);
        BitConverter.TryWriteBytes(e.AsSpan(4), state);
        BitConverter.TryWriteBytes(e.AsSpan(8), r.Type);
        BitConverter.TryWriteBytes(e.AsSpan(16), _now);
        r.Events.Add(e);
        return e;
    }

    static void V2(byte[] e, int off, short x, short y)
    {
        BitConverter.TryWriteBytes(e.AsSpan(off), x);
        BitConverter.TryWriteBytes(e.AsSpan(off + 2), y);
    }

    [Hle]
    int sceSystemGestureUpdateTouchRecognizer(uint rec)
    {
        lock (_lock)
        {
            if (!_recognizers.TryGetValue(rec, out var r)) return ERR_INVALID_ARG;
            r.Events.Clear();
            r.UpdatedTime = _now;
            // reused scratch list (updated every frame per recognizer; no LINQ garbage)
            var mine = _mine;
            mine.Clear();
            foreach (var p in _primitives) if (p.Port == r.Port) mine.Add(p);
            switch (r.Type)
            {
                case TYPE_TAP: UpdateTap(r, mine); break;
                case TYPE_DRAG: UpdateDrag(r, mine); break;
                case TYPE_TAP_AND_HOLD: UpdateHold(r, mine); break;
                case TYPE_PINCH:
                case TYPE_ROTATION: UpdatePinch(r, mine); break;
            }
        }
        return 0;
    }

    readonly List<Primitive> _mine = new(), _live = new();
    readonly List<ushort> _staleTracks = new();

    /// <summary>Forgets tracks whose primitive is gone (same as removing every key with no matching primitive).</summary>
    void RemoveStaleTracks(Recognizer r, List<Primitive> prims)
    {
        _staleTracks.Clear();
        foreach (var k in r.Tracks.Keys)
        {
            bool found = false;
            foreach (var p in prims) if (p.Id == k) { found = true; break; }
            if (!found) _staleTracks.Add(k);
        }
        foreach (var k in _staleTracks) r.Tracks.Remove(k);
    }

    void UpdateTap(Recognizer r, List<Primitive> prims)
    {
        foreach (var p in prims)
        {
            if (p.State != STATE_END || !Inside(r, p.PressX, p.PressY) || !Inside(r, p.CurX, p.CurY)) continue;
            int move = Math.Abs(p.CurX - p.PressX) + Math.Abs(p.CurY - p.PressY);
            if (move > TapMaxMove || _now - p.PressTime > TapMaxTime) continue;
            bool chained = r.TapCount > 0 && r.TapCount < r.MaxTapCount && _now - r.LastTapTime <= MultiTapWindow
                           && Math.Abs(p.CurX - r.LastTapX) + Math.Abs(p.CurY - r.LastTapY) <= TapMaxMove * 2;
            r.TapCount = chained ? r.TapCount + 1 : 1;
            r.LastTapTime = _now;
            r.LastTapX = p.CurX; r.LastTapY = p.CurY;
            var e = NewEvent(r, r.NextEventId++, STATE_END);
            BitConverter.TryWriteBytes(e.AsSpan(24), p.Id);
            V2(e, 26, p.CurX, p.CurY);
            e[30] = (byte)r.TapCount;
            Util.Log.Debug($"gesture tap at ({p.CurX},{p.CurY}) recognizer rect ({r.RX},{r.RY},{r.RW},{r.RH})");
        }
    }

    void UpdateDrag(Recognizer r, List<Primitive> prims)
    {
        foreach (var p in prims)
        {
            r.Tracks.TryGetValue(p.Id, out var t);
            if (t.State == STATE_INACTIVE)
            {
                // a drag starts at touch-down (games derive taps from short begin/end drags)
                if (!Inside(r, p.PressX, p.PressY)) continue;
                t = (STATE_BEGIN, _now, r.NextEventId++);
                if (p.State == STATE_END)
                {
                    // pressed and released between two updates: report the begin and the end together
                    var b = NewEvent(r, t.EventId, STATE_BEGIN);
                    BitConverter.TryWriteBytes(b.AsSpan(24), p.Id);
                    V2(b, 30, p.PressX, p.PressY); V2(b, 34, p.PressX, p.PressY);
                    t.State = STATE_END;
                }
            }
            else t.State = p.State == STATE_END ? STATE_END : STATE_ACTIVE;
            var e = NewEvent(r, t.EventId, t.State);
            Util.Log.Debug($"gesture drag ev {t.EventId} state {t.State} prim {p.Id} at ({p.CurX},{p.CurY})");
            BitConverter.TryWriteBytes(e.AsSpan(24), p.Id);
            V2(e, 26, p.DX, p.DY);
            V2(e, 30, p.CurX, p.CurY);
            V2(e, 34, p.PressX, p.PressY);
            if (t.State == STATE_END) r.Tracks.Remove(p.Id); else r.Tracks[p.Id] = t;
        }
        RemoveStaleTracks(r, prims);
    }

    void UpdateHold(Recognizer r, List<Primitive> prims)
    {
        foreach (var p in prims)
        {
            r.Tracks.TryGetValue(p.Id, out var t);
            int move = Math.Abs(p.CurX - p.PressX) + Math.Abs(p.CurY - p.PressY);
            if (t.State == STATE_INACTIVE)
            {
                if (p.State == STATE_END || !Inside(r, p.PressX, p.PressY) || move > TapMaxMove || _now - p.PressTime < HoldTime) continue;
                t = (STATE_BEGIN, _now, r.NextEventId++);
            }
            else t.State = p.State == STATE_END || move > TapMaxMove ? STATE_END : STATE_ACTIVE;
            var e = NewEvent(r, t.EventId, t.State);
            Util.Log.Debug($"gesture drag ev {t.EventId} state {t.State} prim {p.Id} at ({p.CurX},{p.CurY})");
            BitConverter.TryWriteBytes(e.AsSpan(24), p.Id);
            V2(e, 26, p.PressX, p.PressY);
            if (t.State == STATE_END) r.Tracks[p.Id] = (STATE_END, t.Begin, t.EventId); else r.Tracks[p.Id] = t;
        }
        RemoveStaleTracks(r, prims);
    }

    void UpdatePinch(Recognizer r, List<Primitive> prims)
    {
        var live = _live; // first two primitives pressed inside the recognizer (reused list)
        live.Clear();
        foreach (var p in prims) { if (live.Count == 2) break; if (Inside(r, p.PressX, p.PressY)) live.Add(p); }
        if (live.Count < 2)
        {
            if (r.PinchActive)
            {
                NewEvent(r, r.PinchEventId, STATE_END);
                r.PinchActive = false;
            }
            return;
        }
        float dist = MathF.Sqrt(MathF.Pow(live[0].CurX - live[1].CurX, 2) + MathF.Pow(live[0].CurY - live[1].CurY, 2));
        float angle = MathF.Atan2(live[1].CurY - live[0].CurY, live[1].CurX - live[0].CurX);
        uint state;
        if (!r.PinchActive)
        {
            r.PinchActive = true;
            r.PinchEventId = r.NextEventId++;
            r.PinchStartDist = Math.Max(dist, 1);
            r.RotationStartAngle = angle;
            state = STATE_BEGIN;
        }
        else state = live[0].State == STATE_END || live[1].State == STATE_END ? STATE_END : STATE_ACTIVE;
        var e = NewEvent(r, r.PinchEventId, state);
        // pinch: distance ratio since the begin; rotation: angle (radians, -pi..pi) since the begin
        float value = r.Type == TYPE_ROTATION ? RotationSince(r.RotationStartAngle, angle) : dist / r.PinchStartDist;
        BitConverter.TryWriteBytes(e.AsSpan(24), value);
        for (int i = 0; i < 2; i++)
        {
            int o = 28 + i * 10;
            BitConverter.TryWriteBytes(e.AsSpan(o), live[i].Id);
            V2(e, o + 2, live[i].CurX, live[i].CurY);
            V2(e, o + 6, live[i].DX, live[i].DY);
        }
        if (state == STATE_END) r.PinchActive = false;
    }

    internal static float RotationSince(float start, float now)
    {
        float a = now - start;
        return a > MathF.PI ? a - 2 * MathF.PI : a < -MathF.PI ? a + 2 * MathF.PI : a;
    }

    [Hle]
    int sceSystemGestureGetTouchEvents(uint rec, uint buffer, uint capacity, Ptr<uint> num)
    {
        lock (_lock)
        {
            if (!_recognizers.TryGetValue(rec, out var r)) { num.TrySet(0); return ERR_INVALID_ARG; }
            uint n = 0;
            foreach (var e in r.Events)
            {
                if (n >= capacity) break;
                if (buffer != 0) e.CopyTo(GuestMemory.Span(buffer + n * 144, 144));
                n++;
            }
            num.TrySet(n);
        }
        return 0;
    }

    [Hle] int sceSystemGestureGetTouchEventsCount(uint rec) { lock (_lock) return _recognizers.TryGetValue(rec, out var r) ? r.Events.Count : ERR_INVALID_ARG; }

    [Hle]
    int sceSystemGestureGetTouchEventByIndex(uint rec, uint index, uint dst)
    {
        lock (_lock)
        {
            if (!_recognizers.TryGetValue(rec, out var r) || index >= r.Events.Count || dst == 0) return ERR_INVALID_ARG;
            r.Events[(int)index].CopyTo(GuestMemory.Span(dst, 144));
        }
        return 0;
    }

    [Hle]
    int sceSystemGestureGetTouchEventByEventID(uint rec, uint id, uint dst)
    {
        lock (_lock)
        {
            if (!_recognizers.TryGetValue(rec, out var r) || dst == 0) return ERR_INVALID_ARG;
            var e = r.Events.FirstOrDefault(ev => BitConverter.ToUInt32(ev, 0) == id);
            if (e == null) return ERR_INVALID_ARG;
            e.CopyTo(GuestMemory.Span(dst, 144));
        }
        return 0;
    }

    [Hle]
    int sceSystemGestureGetTouchRecognizerInformation(uint rec, uint info)
    {
        lock (_lock)
        {
            if (!_recognizers.TryGetValue(rec, out var r) || info == 0) return ERR_INVALID_ARG;
            GuestMemory.Fill(info, 0, 280);
            GuestMemory.W32(info, r.Type);
            GuestMemory.W32(info + 4, (uint)r.Port);
            GuestMemory.Write(info + 8, r.RX); GuestMemory.Write(info + 10, r.RY);
            GuestMemory.Write(info + 12, r.RW); GuestMemory.Write(info + 14, r.RH);
            GuestMemory.Write(info + 16, r.UpdatedTime);
        }
        return 0;
    }
}
