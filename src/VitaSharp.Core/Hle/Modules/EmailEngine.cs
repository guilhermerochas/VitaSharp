using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceEmailEngine (vs0:app/NPXS10072/email_engine.suprx 3.74, the Email app's engine): the member accessors of its
/// C++ objects (accounts, folders, messages, parts, sync jobs), which read or write one word of the object, and the
/// release helpers that call an object's deleting destructor (vtable slot 1) when it is not NULL. The exports have no
/// names (NIDs only); each NID's member offset was read in the module. Firmware fallback only: the Email app loads
/// the real engine.
/// </summary>
public sealed partial class EmailEngine : HleModule
{
    const string Lib = "SceEmailEngine";
    public override bool FirmwareFallbackOnly => true;

    static uint R(uint o, uint off) => GuestMemory.R32(o + off);
    static void W(uint o, uint off, uint v) => GuestMemory.W32(o + off, v);

    // ------------------------------------------------------------------ getters

    [Hle("NID_D4476660", Nid = 0xD4476660, Library = Lib)] [Hle("NID_CB4B2DDC", Nid = 0xCB4B2DDC, Library = Lib)]
    [Hle("NID_E496AA91", Nid = 0xE496AA91, Library = Lib)] [Hle("NID_74D6828B", Nid = 0x74D6828B, Library = Lib)]
    [Hle("NID_17D538BC", Nid = 0x17D538BC, Library = Lib)] [Hle("NID_ECCBBB02", Nid = 0xECCBBB02, Library = Lib)]
    [Hle("NID_1F29019A", Nid = 0x1F29019A, Library = Lib)] [Hle("NID_4429D098", Nid = 0x4429D098, Library = Lib)]
    [Hle("NID_C7112777", Nid = 0xC7112777, Library = Lib)] [Hle("NID_104D7544", Nid = 0x104D7544, Library = Lib)]
    [Hle("NID_BDE3C3C6", Nid = 0xBDE3C3C6, Library = Lib)] [Hle("NID_A1CE5306", Nid = 0xA1CE5306, Library = Lib)]
    [Hle("NID_5FFB10EA", Nid = 0x5FFB10EA, Library = Lib)]
    uint Get0C(uint o) => R(o, 0x0C);

    [Hle("NID_C98F6B4A", Nid = 0xC98F6B4A, Library = Lib)] [Hle("NID_E1DC4A2F", Nid = 0xE1DC4A2F, Library = Lib)]
    [Hle("NID_B2F60098", Nid = 0xB2F60098, Library = Lib)] [Hle("NID_FA5D426F", Nid = 0xFA5D426F, Library = Lib)]
    [Hle("NID_1C311182", Nid = 0x1C311182, Library = Lib)] [Hle("NID_75307546", Nid = 0x75307546, Library = Lib)]
    [Hle("NID_B1DEA7C2", Nid = 0xB1DEA7C2, Library = Lib)] [Hle("NID_3B89D1F3", Nid = 0x3B89D1F3, Library = Lib)]
    [Hle("NID_87E99CEC", Nid = 0x87E99CEC, Library = Lib)] [Hle("NID_69DB5835", Nid = 0x69DB5835, Library = Lib)]
    [Hle("NID_E35461B2", Nid = 0xE35461B2, Library = Lib)] [Hle("NID_2CAB4860", Nid = 0x2CAB4860, Library = Lib)]
    [Hle("NID_3C5B6EDE", Nid = 0x3C5B6EDE, Library = Lib)] [Hle("NID_812E7ADA", Nid = 0x812E7ADA, Library = Lib)]
    uint Get10(uint o) => R(o, 0x10);

    [Hle("NID_4E62EF1C", Nid = 0x4E62EF1C, Library = Lib)] [Hle("NID_3C529DB1", Nid = 0x3C529DB1, Library = Lib)]
    [Hle("NID_56C70B77", Nid = 0x56C70B77, Library = Lib)] [Hle("NID_41E65FEE", Nid = 0x41E65FEE, Library = Lib)]
    [Hle("NID_45F97ED8", Nid = 0x45F97ED8, Library = Lib)] [Hle("NID_5272681C", Nid = 0x5272681C, Library = Lib)]
    [Hle("NID_43E6D761", Nid = 0x43E6D761, Library = Lib)] [Hle("NID_6F257A67", Nid = 0x6F257A67, Library = Lib)]
    [Hle("NID_10A83AAB", Nid = 0x10A83AAB, Library = Lib)] [Hle("NID_63D5F9A1", Nid = 0x63D5F9A1, Library = Lib)]
    [Hle("NID_C322E3E1", Nid = 0xC322E3E1, Library = Lib)]
    uint Get14(uint o) => R(o, 0x14);

    [Hle("NID_972944A5", Nid = 0x972944A5, Library = Lib)] [Hle("NID_C3F0A31F", Nid = 0xC3F0A31F, Library = Lib)]
    [Hle("NID_69B5CF08", Nid = 0x69B5CF08, Library = Lib)] [Hle("NID_0A5163BD", Nid = 0x0A5163BD, Library = Lib)]
    [Hle("NID_482CB854", Nid = 0x482CB854, Library = Lib)] [Hle("NID_8D5DEFCC", Nid = 0x8D5DEFCC, Library = Lib)]
    [Hle("NID_214CBCE8", Nid = 0x214CBCE8, Library = Lib)] [Hle("NID_9A5052AB", Nid = 0x9A5052AB, Library = Lib)]
    uint Get18(uint o) => R(o, 0x18);

    [Hle("NID_B38BC5FB", Nid = 0xB38BC5FB, Library = Lib)] [Hle("NID_D5465A42", Nid = 0xD5465A42, Library = Lib)]
    [Hle("NID_DAD41171", Nid = 0xDAD41171, Library = Lib)] [Hle("NID_FB742FE1", Nid = 0xFB742FE1, Library = Lib)]
    [Hle("NID_E3DDA3D6", Nid = 0xE3DDA3D6, Library = Lib)] [Hle("NID_64D0F044", Nid = 0x64D0F044, Library = Lib)]
    uint Get1C(uint o) => R(o, 0x1C);

    [Hle("NID_BD00A9F1", Nid = 0xBD00A9F1, Library = Lib)] [Hle("NID_D4411D62", Nid = 0xD4411D62, Library = Lib)]
    [Hle("NID_8054EA5E", Nid = 0x8054EA5E, Library = Lib)] [Hle("NID_A377D285", Nid = 0xA377D285, Library = Lib)]
    uint Get20(uint o) => R(o, 0x20);

    [Hle("NID_04BD344B", Nid = 0x04BD344B, Library = Lib)] [Hle("NID_6D662F64", Nid = 0x6D662F64, Library = Lib)]
    [Hle("NID_B629A66C", Nid = 0xB629A66C, Library = Lib)] [Hle("NID_A502374C", Nid = 0xA502374C, Library = Lib)]
    uint Get24(uint o) => R(o, 0x24);

    [Hle("NID_8D7D8D85", Nid = 0x8D7D8D85, Library = Lib)] [Hle("NID_590B41AF", Nid = 0x590B41AF, Library = Lib)]
    [Hle("NID_6438F4A9", Nid = 0x6438F4A9, Library = Lib)]
    uint Get28(uint o) => R(o, 0x28);

    [Hle("NID_246C1BD5", Nid = 0x246C1BD5, Library = Lib)] [Hle("NID_43EFFE97", Nid = 0x43EFFE97, Library = Lib)]
    [Hle("NID_C75ACB68", Nid = 0xC75ACB68, Library = Lib)]
    uint Get2C(uint o) => R(o, 0x2C);

    [Hle("NID_4B69C62C", Nid = 0x4B69C62C, Library = Lib)] [Hle("NID_0B1CF007", Nid = 0x0B1CF007, Library = Lib)]
    [Hle("NID_B2EBD24A", Nid = 0xB2EBD24A, Library = Lib)]
    uint Get30(uint o) => R(o, 0x30);

    [Hle("NID_4F4F2193", Nid = 0x4F4F2193, Library = Lib)] uint Get34(uint o) => R(o, 0x34);
    [Hle("NID_2E77130B", Nid = 0x2E77130B, Library = Lib)] uint Get38(uint o) => R(o, 0x38);
    [Hle("NID_01C6A0FE", Nid = 0x01C6A0FE, Library = Lib)] uint Get3C(uint o) => R(o, 0x3C);
    [Hle("NID_4B3D6CEC", Nid = 0x4B3D6CEC, Library = Lib)] uint Get40(uint o) => R(o, 0x40);
    [Hle("NID_32FC88E4", Nid = 0x32FC88E4, Library = Lib)] uint Get44(uint o) => R(o, 0x44);
    [Hle("NID_5D97FFED", Nid = 0x5D97FFED, Library = Lib)] uint Get48(uint o) => R(o, 0x48);
    [Hle("NID_FE3B8BA8", Nid = 0xFE3B8BA8, Library = Lib)] uint Get4C(uint o) => R(o, 0x4C);
    [Hle("NID_8767E9A4", Nid = 0x8767E9A4, Library = Lib)] uint Get60(uint o) => R(o, 0x60);
    [Hle("NID_D9C993DF", Nid = 0xD9C993DF, Library = Lib)] uint Get68(uint o) => R(o, 0x68);
    [Hle("NID_7FFCAF41", Nid = 0x7FFCAF41, Library = Lib)] uint Get6C(uint o) => R(o, 0x6C);
    [Hle("NID_00EB5866", Nid = 0x00EB5866, Library = Lib)] uint Get80(uint o) => R(o, 0x80);

    // ------------------------------------------------------------------ setters

    [Hle("NID_85220BD4", Nid = 0x85220BD4, Library = Lib)] [Hle("NID_62BB1463", Nid = 0x62BB1463, Library = Lib)]
    void Set0C(uint o, uint v) => W(o, 0x0C, v);
    [Hle("NID_121374D2", Nid = 0x121374D2, Library = Lib)] void Set14(uint o, uint v) => W(o, 0x14, v);
    [Hle("NID_5FCE13FC", Nid = 0x5FCE13FC, Library = Lib)] void Set18(uint o, uint v) => W(o, 0x18, v);
    [Hle("NID_337910A0", Nid = 0x337910A0, Library = Lib)] void Set24(uint o, uint v) => W(o, 0x24, v);
    [Hle("NID_0C32AA66", Nid = 0x0C32AA66, Library = Lib)] void Set28(uint o, uint v) => W(o, 0x28, v);
    [Hle("NID_FACC67AC", Nid = 0xFACC67AC, Library = Lib)] void Set2C(uint o, uint v) => W(o, 0x2C, v);
    [Hle("NID_6BD44F0D", Nid = 0x6BD44F0D, Library = Lib)] void Set30(uint o, uint v) => W(o, 0x30, v);
    [Hle("NID_832426CF", Nid = 0x832426CF, Library = Lib)] void Set3C(uint o, uint v) => W(o, 0x3C, v);
    [Hle("NID_59C58A43", Nid = 0x59C58A43, Library = Lib)] void Set40(uint o, uint v) => W(o, 0x40, v);
    [Hle("NID_48F0095D", Nid = 0x48F0095D, Library = Lib)] void Set44(uint o, uint v) => W(o, 0x44, v);
    [Hle("NID_6A2FE369", Nid = 0x6A2FE369, Library = Lib)] void Set48(uint o, uint v) => W(o, 0x48, v);

    // ------------------------------------------------------------------ release helpers

    /// <summary>(Object* o): delete o (its deleting destructor, vtable slot 1) unless it is NULL.</summary>
    [Hle("NID_621D8498", Nid = 0x621D8498, Library = Lib)] [Hle("NID_66805192", Nid = 0x66805192, Library = Lib)]
    [Hle("NID_5306EEA4", Nid = 0x5306EEA4, Library = Lib)] [Hle("NID_86D2F016", Nid = 0x86D2F016, Library = Lib)]
    [Hle("NID_1655DFDE", Nid = 0x1655DFDE, Library = Lib)] [Hle("NID_CDAFC449", Nid = 0xCDAFC449, Library = Lib)]
    void Release(uint o)
    {
        if (o != 0) GuestThread.Current!.CallGuest(GuestMemory.R32(GuestMemory.R32(o) + 4), o);
    }
}
