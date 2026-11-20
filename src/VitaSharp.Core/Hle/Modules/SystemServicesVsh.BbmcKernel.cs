using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceBbmcForDriver (os0:bootimage SceBbmc 3.74, read in the module, study only): the 3G modem (baseband) manager's
// kernel entries (SceSblUpdateMgr uses them to update the modem's firmware). The module starts its state only when the
// syscon hardware info has the modem bit (bit 1) and no USB debug module runs; on the emulated unit (a Wi-Fi
// PCH-1000, no modem) module_start returns without it and the state pointer stays NULL, so every entry answers
// 0x80330003 (not initialized) as the module does; the four whose data pointer is checked first answer 0x80330002
// for a NULL one. Two entries read the NULL state without checking it (46C5F794, 44217A55: a kernel data abort on
// the console); they answer 0x80330003 like their siblings. The modem power line (GPIO port 0, pin 0x1E) is a
// reference count the module keeps with or without a modem: D98DB926 takes a reference, D16D0845 drops one.
public sealed partial class SystemServicesVsh
{
    const int BBMC_ERR_INVALID_ARGUMENT = unchecked((int)0x80330002);

    readonly object _bbmcLock = new();

    /// <summary>References on the modem power line (GPIO port 0, pin 0x1E).</summary>
    [SnapshotMissing] // states of builds before it: no reference
    int _bbmcPowerReferences;

    /// <summary>The modem power line's references (the line is on while above 0).</summary>
    public int BbmcPowerReferences
    {
        get
        {
            lock (_bbmcLock)
            {
                return _bbmcPowerReferences;
            }
        }
    }

    /// <summary>The entries that check the modem state first: 0x80330003 (no modem).</summary>
    [Hle(Nid = 0x009C73D2, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x07ADDA90, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x0819A4DD, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x0AF6520F, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x192D641F, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x1B122CE5, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x24EE6E9C, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x2930DB51, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x2CD4131A, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x393E70CB, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x399EF002, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x3D06FDB0, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x3D27000A, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x435858B1, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x44217A55, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x454DF20B, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x46C5F794, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x503C7E0B, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x580A3C06, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x683D19BF, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x6A56F88C, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x6AD4053A, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x6F3697F6, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x739274F3, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x75FA68D3, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x77030C3C, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x7CB72009, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x7EA964B6, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x864E6CA0, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x8820F7C4, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x8AAC6F1E, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x902D3CAC, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x92D7C12C, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x93E5345A, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x94A31A9C, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x9611C253, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xA41FA509, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xA5E182D0, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xA9467106, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xA9CF664F, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xACB0EC17, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xB17A056F, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xB7E650A9, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xBC3C8942, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xBF550F4A, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xC35ECB1F, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xC835DB15, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xD288E771, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xDCE38C84, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xE09808A2, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xE39F8435, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xE767F32C, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xE83B73CA, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xEBC5D3DB, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xF2DABC5B, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xF4C8482D, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xFB3DA35E, Library = "SceBbmcForDriver", Default = true)]
    int ksceBbmcNoModem()
    {
        return BBMC_ERR_NO_MODEM;
    }

    /// <summary>(id, void *data): the entries that check their data pointer before the modem state: NULL is
    /// 0x80330002, else 0x80330003 (no modem).</summary>
    [Hle(Nid = 0x891AFA0C, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xE89B746D, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0x4281E476, Library = "SceBbmcForDriver", Default = true)]
    [Hle(Nid = 0xFCF6A1EA, Library = "SceBbmcForDriver", Default = true)]
    int ksceBbmcNoModemData(uint id, uint data)
    {
        return data == 0 ? BBMC_ERR_INVALID_ARGUMENT : BBMC_ERR_NO_MODEM;
    }

    /// <summary>NID_D98DB926(): takes a reference on the modem power line (the first one turns it on); 0.</summary>
    [Hle(Nid = 0xD98DB926, Library = "SceBbmcForDriver")]
    int ksceBbmcPowerLineAcquire()
    {
        lock (_bbmcLock)
        {
            _bbmcPowerReferences++;
            return 0;
        }
    }

    /// <summary>NID_D16D0845(): drops a reference on the modem power line (the last one turns it off; none held: nothing
    /// changes); 0.</summary>
    [Hle(Nid = 0xD16D0845, Library = "SceBbmcForDriver")]
    int ksceBbmcPowerLineRelease()
    {
        lock (_bbmcLock)
        {
            if (_bbmcPowerReferences > 0)
            {
                _bbmcPowerReferences--;
            }

            return 0;
        }
    }
}
