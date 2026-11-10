using System.Text;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceMtpClient (vs0:vsh/common/mtp_client.suprx): the client side of the shell's SceMtpService, the C++ class the
/// system applications (Content Manager's helpers, the media content handler, gamedata, np_kdc) use to run MTP
/// operations through the shell. Every method takes the client object: +0 the IPC client of SceMtpService
/// (0 until Initialize connected it), +4 the event client, +8 the operation tickets, +0x48 the status listener,
/// +0x4C the selected Wi-Fi host, +0x50 the background listener; each request is one synchronous IPC call
/// (method 0x1234xxxx, the number in the attribute comments). Behaviour read in the module (FW 3.74); the names are
/// the module's log strings, the error codes and their texts (GetErrorString) its own.
/// <para>The HLE has no IPC client for SceMtpService, so Initialize cannot connect (MTP_SERVICE_ERROR_NOTFOUND) and
/// the object stays unconnected: every request then fails with the library's first check,
/// MTP_SERVICE_ERROR_NOTINIT (0x80540107), as on hardware with the service gone. The parts that only touch the
/// object (listeners, the status and background observers, the error strings) work.</para>
/// A system program keeps the firmware's library.
/// </summary>
public sealed partial class MtpClient : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const string Lib = "SceMtpClient";
    const int NOMEM = unchecked((int)0x80540101), INVAL = unchecked((int)0x80540102), NOTFOUND = unchecked((int)0x80540105),
        ALREADY = unchecked((int)0x80540106), NOTINIT = unchecked((int)0x80540107);

    HleGuestStubs? _stubs;
    HleGuestStubs Stubs => _stubs ??= new HleGuestStubs(this, Lib);

    static uint U(uint a) => GuestMemory.R32(a);
    static void U(uint a, uint v) => GuestMemory.W32(a, v);

    /// <summary>The library's first check: the object's SceMtpService client must exist.</summary>
    static int Connected(uint me) => me == 0 || U(me) == 0 ? NOTINIT : 0;

    // ---- the client object

    /// <summary>(this): connects to SceMtpService (two IPC clients, the event thread). Not reachable from the HLE.</summary>
    [Hle("MtpClient::Initialize", Nid = 0x99E4CEED, Library = Lib)]
    int ClientInitialize(uint me)
    {
        if (me == 0) return INVAL;
        if (U(me) != 0) return ALREADY;
        U(me + 4, 0);
        U(me + 0x54, uint.MaxValue);
        U(me + 0x5C, uint.MaxValue);
        return NOTFOUND;
    }

    /// <summary>(this): disconnects both IPC clients and ends the event thread.</summary>
    [Hle("MtpClient::Finalize", Nid = 0x8313A28C, Library = Lib)]
    int ClientFinalize(uint me) => me == 0 || U(me) == 0 || U(me + 4) == 0 ? NOTINIT : 0;

    /// <summary>(this) → this: the destructor (finalizes a connected client, empties the request map).</summary>
    [Hle("MtpClient::~MtpClient", Nid = 0x90A60690, Library = Lib)]
    uint Destroy(uint me)
    {
        U(me + 0x3C, uint.MaxValue);
        U(me + 0x40, uint.MaxValue);
        return me;
    }

    /// <summary>(this, listener): one status listener per client.</summary>
    [Hle("MtpClient::SetStatusListener", Nid = 0xC15D8AAB, Library = Lib)]
    int SetStatusListener(uint me, uint listener)
    {
        if (listener == 0) return INVAL;
        if (U(me + 0x48) != 0) return ALREADY;
        U(me + 0x48, listener);
        return 0;
    }

    [Hle("MtpClient::ClearStatusListener", Nid = 0xD0C57E1A, Library = Lib)]
    int ClearStatusListener(uint me)
    {
        if (U(me + 0x48) == 0) return ALREADY;
        U(me + 0x48, 0);
        return 0;
    }

    static int SetBgListener(uint client, uint listener)
    {
        if (listener == 0) return INVAL;
        if (U(client + 0x50) != 0) return ALREADY;
        U(client + 0x50, listener);
        return 0;
    }

    static int ClearBgListener(uint client)
    {
        if (U(client + 0x50) == 0) return ALREADY;
        U(client + 0x50, 0);
        return 0;
    }

    /// <summary>(this, ticket): a request on the event client (+4), method 0x12340040.</summary>
    [Hle("MtpClient::EventCall40", Nid = 0x6AF22A07, Library = Lib)]
    int EventCall40(uint me, uint ticket) => NOTINIT;

    /// <summary>(this, char *buf, SceSize size, int *len, int timeout): reads a message the service pushed.</summary>
    [Hle("MtpClient::GetMsgFromServer", Nid = 0x6A9B661B, Library = Lib)]
    int GetMsgFromServer(uint me, uint a, uint b, uint c, uint d) => a == 0 || b == 0 || c == 0 ? INVAL : Connected(me);

    /// <summary>(this, mask, mode, int *resultPattern): waits on the service's background event flag.</summary>
    [Hle("MtpClient::WaitRegisterBgEventFlag", Nid = 0x5EE32DB6, Library = Lib)]
    int WaitRegisterBgEventFlag(uint me, uint mask, uint mode, uint result) => result == 0 ? INVAL : Connected(me);

    /// <summary>Requests without the library's own check (the IPC call runs on the client directly).</summary>
    [Hle("MtpClient::ServiceCall64", Nid = 0x2D784D2F, Library = Lib)] // method 0x12340064
    [Hle("MtpClient::ServiceCall84", Nid = 0x596BE5C8, Library = Lib)] // method 0x12340084
    [Hle("MtpClient::ServiceCall85", Nid = 0x310B7721, Library = Lib)] // method 0x12340085
    [Hle("MtpClient::ServiceCall6C", Nid = 0xD47C33B5, Library = Lib)] // method 0x1234006C
    int DirectCall(uint me) => Connected(me);

    /// <summary>Every other request of the client (operations on the PC, host and setting queries, the RPC sockets of
    /// the network library, the Wi-Fi host calls): the service must be connected.</summary>
    [Hle("MtpClient::BeginGetNumOfObject", Nid = 0xB59EF409, Library = Lib)] // method 0x12340002
    [Hle("MtpClient::EndGetNumOfObject", Nid = 0x91EE6358, Library = Lib)] // method 0x12340003
    [Hle("MtpClient::BeginGetObjectMetadata", Nid = 0xD560E789, Library = Lib)] // method 0x12340004
    [Hle("MtpClient::ServiceCall003C", Nid = 0x041DE727, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::EndGetObjectMetadata", Nid = 0x76323C27, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::BeginImportObject", Nid = 0x5D58CAC8, Library = Lib)] // method 0x12340006
    [Hle("MtpClient::EndImportObject", Nid = 0x5A91E79A, Library = Lib)] // method 0x12340007
    [Hle("MtpClient::BeginExportObjectWithCheck", Nid = 0x4CFAD42B, Library = Lib)] // method 0x12340008
    [Hle("MtpClient::EndExportObject", Nid = 0x7146806F, Library = Lib)] // method 0x12340009
    [Hle("MtpClient::BeginCreateHostDir", Nid = 0x48C9F280, Library = Lib)] // method 0x1234000a
    [Hle("MtpClient::EndCreateHostDir", Nid = 0x302DD6F9, Library = Lib)] // method 0x1234000b
    [Hle("MtpClient::BeginRenameHostDir", Nid = 0x5F67E346, Library = Lib)] // method 0x1234000c
    [Hle("MtpClient::EndRenameHostDir", Nid = 0x8BEA9700, Library = Lib)] // method 0x1234000d
    [Hle("MtpClient::BeginMoveHostDir", Nid = 0x009B1959, Library = Lib)] // method 0x1234000e
    [Hle("MtpClient::EndMoveHostDir", Nid = 0x56159D40, Library = Lib)] // method 0x1234000f
    [Hle("MtpClient::BeginCreateHostFile", Nid = 0x25FA91E0, Library = Lib)] // method 0x12340010
    [Hle("MtpClient::EndCreateHostFile", Nid = 0xA17E7303, Library = Lib)] // method 0x12340011
    [Hle("MtpClient::BeginGetObjectThumbnail", Nid = 0xA1567EB4, Library = Lib)] // method 0x12340012
    [Hle("MtpClient::EndGetObjectThumbnail", Nid = 0xE8592D2B, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::BeginGetObjectStatus", Nid = 0xBD00B140, Library = Lib)] // method 0x12340014
    [Hle("MtpClient::EndGetObjectStatus", Nid = 0x38855715, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::BeginDeleteObject", Nid = 0x13F702BF, Library = Lib)] // method 0x12340016
    [Hle("MtpClient::EndDeleteObject", Nid = 0xF7AC9BB2, Library = Lib)] // method 0x12340017
    [Hle("MtpClient::BeginHttpGetDataWithUrl", Nid = 0xED1009CB, Library = Lib)] // method 0x12340018
    [Hle("MtpClient::EndHttpGetDataWithUrl", Nid = 0x9BE13016, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::BeginHttpGetPropertyWithUrl", Nid = 0x9FBD0560, Library = Lib)] // method 0x1234001a
    [Hle("MtpClient::EndHttpGetPropertyWithUrl", Nid = 0x6053FA22, Library = Lib)] // method 0x1234001b
    [Hle("MtpClient::BeginGetSystemSetting", Nid = 0x3BD81AD9, Library = Lib)] // method 0x1234001c
    [Hle("MtpClient::EndGetSystemSetting", Nid = 0xF68A4C8D, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::BeginSetSystemSetting", Nid = 0xCBA778A1, Library = Lib)] // method 0x1234001e
    [Hle("MtpClient::EndSetSystemSetting", Nid = 0x64B12F0C, Library = Lib)] // method 0x1234001f
    [Hle("MtpClient::BeginGetNpAccountInfo", Nid = 0xC2AE9036, Library = Lib)] // method 0x12340020
    [Hle("MtpClient::EndGetNpAccountInfo", Nid = 0x33BBDBF5, Library = Lib)] // method 0x12340021
    [Hle("MtpClient::BeginReadObjectWithOffset", Nid = 0xE59BD57C, Library = Lib)] // method 0x12340022
    [Hle("MtpClient::EndReadObjectWithOffset", Nid = 0xFD780F8B, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::BeginWriteObjectWithOffset", Nid = 0x4F90EE05, Library = Lib)] // method 0x12340024
    [Hle("MtpClient::EndWriteObjectWithOffset", Nid = 0x81C63E0D, Library = Lib)] // method 0x12340025
    [Hle("MtpClient::BeginGetHostStorageSize", Nid = 0xD637EAE2, Library = Lib)] // method 0x12340026
    [Hle("MtpClient::EndGetHostStorageSize", Nid = 0x6F043EE9, Library = Lib)] // method 0x12340027
    [Hle("MtpClient::BeginCheckSameObjectExist", Nid = 0x916ABAE8, Library = Lib)] // method 0x12340028
    [Hle("MtpClient::EndCheckSameObjectExist", Nid = 0x8B80B696, Library = Lib)] // method 0x12340029
    [Hle("MtpClient::EndGetTotalObjectSize", Nid = 0x7B05E3FD, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::BeginGetTotalObjectSize", Nid = 0x3828F8D2, Library = Lib)] // method 0x1234002a
    [Hle("MtpClient::BeginGetSpecifiedObjectMetadata", Nid = 0x0ADF80E0, Library = Lib)] // method 0x1234002c
    [Hle("MtpClient::EndGetSpecifiedObjectMetadata", Nid = 0xA0607634, Library = Lib)] // method 0x1234003c
    [Hle("MtpClient::BeginNpDrmGetRtc", Nid = 0xBE451FA4, Library = Lib)] // method 0x1234002e
    [Hle("MtpClient::EndNpDrmGetRtc", Nid = 0xF7DA2EA7, Library = Lib)] // method 0x1234002f
    [Hle("MtpClient::BeginNpDrmActivate", Nid = 0x9920926A, Library = Lib)] // method 0x12340030
    [Hle("MtpClient::EndNpDrmActivate", Nid = 0x1EA892F7, Library = Lib)] // method 0x12340031
    [Hle("MtpClient::BeginNpDrmDeactivate", Nid = 0x97C8E4A1, Library = Lib)] // method 0x12340032
    [Hle("MtpClient::EndNpDrmDeactivate", Nid = 0x4E2FECCD, Library = Lib)] // method 0x12340033
    [Hle("MtpClient::BeginNpDrmGetLicense", Nid = 0xBFB9E02B, Library = Lib)] // method 0x12340034
    [Hle("MtpClient::EndNpDrmGetLicense", Nid = 0x0FF15EF8, Library = Lib)] // method 0x12340035
    [Hle("MtpClient::RecvData", Nid = 0xC4CDB80A, Library = Lib)] // method 0x12340036
    [Hle("MtpClient::SendData", Nid = 0x2E21C90B, Library = Lib)] // method 0x12340039
    [Hle("MtpClient::ServiceCall003D", Nid = 0x84B9E2B5, Library = Lib)] // method 0x1234003d
    [Hle("MtpClient::ServiceCall003E", Nid = 0xBF23FD43, Library = Lib)] // method 0x1234003e
    [Hle("MtpClient::ServiceCall003F", Nid = 0xB0C21E0C, Library = Lib)] // method 0x1234003f
    [Hle("MtpClient::ServiceCall0041", Nid = 0x9C1F2B96, Library = Lib)] // method 0x12340041
    [Hle("MtpClient::ServiceCall0042", Nid = 0xF1D7DF6A, Library = Lib)] // method 0x12340042
    [Hle("MtpClient::ServiceCall0043", Nid = 0x39FD740C, Library = Lib)] // method 0x12340043
    [Hle("MtpClient::ServiceCall0044", Nid = 0xAA493244, Library = Lib)] // method 0x12340044
    [Hle("MtpClient::ServiceCall0045", Nid = 0xEDE2B35C, Library = Lib)] // method 0x12340045
    [Hle("MtpClient::ServiceCall0046", Nid = 0x5D0B186B, Library = Lib)] // method 0x12340046
    [Hle("MtpClient::ServiceCall0047", Nid = 0x916AB0C9, Library = Lib)] // method 0x12340047
    [Hle("MtpClient::ServiceCall0048", Nid = 0x0B691312, Library = Lib)] // method 0x12340048
    [Hle("MtpClient::ServiceCall0049", Nid = 0x3D16DCB7, Library = Lib)] // method 0x12340049
    [Hle("MtpClient::ServiceCall004A", Nid = 0x566A3331, Library = Lib)] // method 0x1234004a
    [Hle("MtpClient::ServiceCall004B", Nid = 0x2AC9277A, Library = Lib)] // method 0x1234004b
    [Hle("MtpClient::ServiceCall004C", Nid = 0xCE97EADE, Library = Lib)] // method 0x1234004c
    [Hle("MtpClient::ServiceCall004D", Nid = 0x171023EA, Library = Lib)] // method 0x1234004d
    [Hle("MtpClient::ServiceCall005F", Nid = 0x77A32E52, Library = Lib)] // method 0x1234005f
    [Hle("MtpClient::ServiceCall0060", Nid = 0xD3DE318D, Library = Lib)] // method 0x12340060
    [Hle("MtpClient::ServiceCall0061", Nid = 0x2090F18E, Library = Lib)] // method 0x12340061
    [Hle("MtpClient::ServiceCall0062", Nid = 0xEFC0794A, Library = Lib)] // method 0x12340062
    [Hle("MtpClient::ServiceCall0063", Nid = 0x9AF1B427, Library = Lib)] // method 0x12340063
    [Hle("MtpClient::ServiceCall64", Nid = 0x6409CD0F, Library = Lib)] // method 0x12340064
    [Hle("MtpClient::ServiceCall0065", Nid = 0x7383EC49, Library = Lib)] // method 0x12340065
    [Hle("MtpClient::ServiceCall0066", Nid = 0x51AC1C9D, Library = Lib)] // method 0x12340066
    [Hle("MtpClient::ServiceCall0067", Nid = 0x54FD4700, Library = Lib)] // method 0x12340067
    [Hle("MtpClient::ServiceCall004E", Nid = 0xB69C2081, Library = Lib)] // method 0x1234004e
    [Hle("MtpClient::ServiceCall004F", Nid = 0xFD3F7F71, Library = Lib)] // method 0x1234004f
    [Hle("MtpClient::ServiceCall0050", Nid = 0x60CC8042, Library = Lib)] // method 0x12340050
    [Hle("MtpClient::ServiceCall0051", Nid = 0xB4097DE2, Library = Lib)] // method 0x12340051
    [Hle("MtpClient::ServiceCall0052", Nid = 0xF1E562CB, Library = Lib)] // method 0x12340052
    [Hle("MtpClient::ServiceCall0053", Nid = 0xFCEBB14C, Library = Lib)] // method 0x12340053
    [Hle("MtpClient::ServiceCall0054", Nid = 0xA93340CA, Library = Lib)] // method 0x12340054
    [Hle("MtpClient::ServiceCall0055", Nid = 0x656178D1, Library = Lib)] // method 0x12340055
    [Hle("MtpClient::ServiceCall0056", Nid = 0x6D71BBD2, Library = Lib)] // method 0x12340056
    [Hle("MtpClient::ServiceCall0057", Nid = 0x6C381167, Library = Lib)] // method 0x12340057
    [Hle("MtpClient::ServiceCall0058", Nid = 0xC6798CBE, Library = Lib)] // method 0x12340058
    [Hle("MtpClient::ServiceCall0059", Nid = 0xC948D975, Library = Lib)] // method 0x12340059
    [Hle("MtpClient::ServiceCall005A", Nid = 0x4EFD945B, Library = Lib)] // method 0x1234005a
    [Hle("MtpClient::ServiceCall005B", Nid = 0x2580EB16, Library = Lib)] // method 0x1234005b
    [Hle("MtpClient::ServiceCall005C", Nid = 0x6ADD7B54, Library = Lib)] // method 0x1234005c
    [Hle("MtpClient::ServiceCall005D", Nid = 0x58CFD938, Library = Lib)] // method 0x1234005d
    [Hle("MtpClient::ServiceCall005E", Nid = 0x406E3F0E, Library = Lib)] // method 0x1234005e
    [Hle("MtpClient::NetMtpConnectToHost", Nid = 0x402A9AAE, Library = Lib)] // method 0x12340076
    [Hle("MtpClient::ServiceCall0078", Nid = 0x0456BF30, Library = Lib)] // method 0x12340078
    [Hle("MtpClient::NetMtpHandover", Nid = 0xAA2F9599, Library = Lib)] // method 0x12340079
    [Hle("MtpClient::ServiceCall007B", Nid = 0x42ED91E1, Library = Lib)] // method 0x1234007b
    [Hle("MtpClient::ServiceCall0069", Nid = 0xB62AE7A2, Library = Lib)] // method 0x12340069
    [Hle("MtpClient::ServiceCall006A", Nid = 0xBB38FFFC, Library = Lib)] // method 0x1234006a
    [Hle("MtpClient::ServiceCall006B", Nid = 0x3B7F84F1, Library = Lib)] // method 0x1234006b
    [Hle("MtpClient::MtpRpcNetSocket", Nid = 0x9500AEEB, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetConnect", Nid = 0x28F0B736, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetSend", Nid = 0x147CC515, Library = Lib)] // method 0x1234006f
    [Hle("MtpClient::MtpRpcNetRecv", Nid = 0x59CBD28C, Library = Lib)] // method 0x12340071
    [Hle("MtpClient::MtpRpcNetSocketClose", Nid = 0x9C9883FF, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetSocketAbort", Nid = 0x06E9025A, Library = Lib)] // method 0x12340073
    [Hle("MtpClient::MtpRpcNetSetsockopt", Nid = 0x52167E75, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetGetsockopt", Nid = 0x7B0C94E5, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetResolverCreate", Nid = 0xCCB7C9B5, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetResolverDestroy", Nid = 0x4CDABD3F, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetResolverStartNtoa", Nid = 0xF28AF4A1, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetResolverGetError", Nid = 0x16C26F25, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetResolverAbort", Nid = 0x14D0CACF, Library = Lib)] // method 0x12340073
    [Hle("MtpClient::MtpRpcNetEpollCreate", Nid = 0x0F39461E, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetEpollControl", Nid = 0x6B78A887, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetEpollWait", Nid = 0x26C6AE43, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetEpollDestroy", Nid = 0x0CABBC43, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetEpollAbort", Nid = 0xB62D1C94, Library = Lib)] // method 0x12340073
    [Hle("MtpClient::MtpRpcNetShutdown", Nid = 0x86B75542, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetGetErrorNo", Nid = 0x8760323B, Library = Lib)] // method 0x1234006d
    [Hle("MtpClient::MtpRpcNetGetSockInfo", Nid = 0xD34C7B95, Library = Lib)] // method 0x12340071
    [Hle("MtpClient::BeginSearchObject", Nid = 0xFDDEF602, Library = Lib)] // method 0x12340074
    [Hle("MtpClient::EndSearchObject", Nid = 0x8D80C937, Library = Lib)] // method 0x12340075
    [Hle("MtpClient::PauseExportObject", Nid = 0x38241E4E, Library = Lib)] // method 0x1234007c
    [Hle("MtpClient::BeginResumeExportObject", Nid = 0x618CB42D, Library = Lib)] // method 0x1234007d
    [Hle("MtpClient::EndResumeExportObject", Nid = 0x661FE8F7, Library = Lib)] // method 0x1234007e
    [Hle("MtpClient::BeginResumeImportObject", Nid = 0x28B9EFE6, Library = Lib)] // method 0x1234007f
    [Hle("MtpClient::EndResumeImportObject", Nid = 0xB33EF208, Library = Lib)] // method 0x12340080
    [Hle("MtpClient::ServiceCall0082", Nid = 0xE3481E4D, Library = Lib)] // method 0x12340082
    [Hle("MtpClient::ServiceCall0083", Nid = 0x9F4CECB6, Library = Lib)] // method 0x12340083
    [Hle("MtpClient::ServiceCall0086", Nid = 0xD9D5892C, Library = Lib)] // method 0x12340086
    [Hle("MtpClient::ServiceCall0087", Nid = 0xE7ACF409, Library = Lib)] // method 0x12340087
    [Hle("MtpClient::ServiceCall0089", Nid = 0xB12F38B8, Library = Lib)] // method 0x12340089
    [Hle("MtpClient::ServiceCall008A", Nid = 0x4FF4AB63, Library = Lib)] // method 0x1234008a
    [Hle("MtpClient::ServiceCall008B", Nid = 0x2957D9DA, Library = Lib)] // method 0x1234008b
    [Hle("MtpClient::ServiceCall0088", Nid = 0x93BF786F, Library = Lib)] // method 0x12340088
    [Hle("MtpClient::ServiceCall008C", Nid = 0x513E056C, Library = Lib)] // method 0x1234008c
    int Request(uint me) => Connected(me);

    // ---- error strings

    static readonly Dictionary<uint, string> ErrorNames = new()
    {
        [0x80540001] = "MTP general error",
        [0x80540002] = "MTP already initialized, invoked etc.",
        [0x80540003] = "MTP invalid argument(s)",
        [0x80540004] = "MTP in progress",
        [0x80540005] = "MTP no space (storage)",
        [0x80540006] = "MTP no entries",
        [0x80540007] = "MTP no memory",
        [0x80540008] = "MTP not available",
        [0x80540009] = "MTP there's no parent",
        [0x8054000A] = "MTP no permission",
        [0x8054000B] = "MTP not supported format",
        [0x8054000C] = "MTP not supported property",
        [0x8054000D] = "MTP there's no received command",
        [0x8054000E] = "MTP handle error",
        [0x8054000F] = "MTP Extended Encryption Data not found",
        [0x80540010] = "MTP License not found",
        [0x80540011] = "MTP not found",
        [0x80540012] = "MTP i/f transfer failed",
        [0x80540013] = "MTP no data",
        [0x80540014] = "MTP terminated",
        [0x80540015] = "MTP host general error",
        [0x80540016] = "MTP host network is disconnected",
        [0x80540017] = "MTP http server is not responding",
        [0x80540018] = "MTP http response is timed out",
        [0x80540019] = "MTP http server is maintained",
        [0x8054001A] = "MTP execution is canceled",
        [0x8054001B] = "MTP NO_HOST",
        [0x8054001C] = "MTP NO_RESPONSE",
        [0x8054001D] = "MTP VERSION_LOW",
        [0x8054001E] = "MTP VERSION_HIGH",
        [0x8054001F] = "MTP PC_DISCONNECTED",
        [0x80540020] = "MTP PS3_DISCONNECTED",
        [0x80540021] = "MTP HOST_BUSY",
        [0x80540022] = "MTP HOST_RECONSTRUCTION",
        [0x80540023] = "MTP INVALID_ACCOUNT",
        [0x80540024] = "MTP OBJECT_EXIST",
        [0x80540025] = "MTP NOT_EXIST",
        [0x80540026] = "MTP NEED_PC_UPDATE",
        [0x80540027] = "MTP NEED_PS3_UPDATE",
        [0x80540028] = "MTP PC_NO_RESPONSE",
        [0x80540029] = "MTP PS3_NO_RESPONSE",
        [0x8054002A] = "MTP PC_TERMINATED",
        [0x8054002B] = "MTP PS3_TERMINATED",
        [0x8054002C] = "MTP OBJECT_READONLY",
        [0x8054002D] = "MTP FOLDER_READONLY",
        [0x8054002E] = "MTP STORAGE_UNAVAILABLE",
        [0x8054002F] = "MTP HOST_NOSPC",
        [0x80540030] = "MTP HOST_OBJECT_FULL",
        [0x80540031] = "MTP HTTP_DONWLOAD_FAILED",
        [0x80540032] = "MTP DB_CORRUPTED",
        [0x80540033] = "MTP NP_FAILED",
        [0x80540034] = "MTP USB_DISCONNECTED",
        [0x80540035] = "MTP DB_CONSTRAINT",
        [0x80540036] = "MTP HOST_INVALID",
        [0x80540037] = "MTP NOT_READY",
        [0x80540038] = "MTP HOST_NOMEM",
        [0x80540039] = "MTP HOST_PERMISSION",
        [0x8054003A] = "MTP BUSY_OBJECT",
        [0x8054003B] = "MTP LOCKED_OBJECT",
        [0x8054003C] = "MTP EXCEEDED_SIZE",
        [0x8054003D] = "MTP DB_FAIL",
        [0x8054003E] = "MTP HOST_OPERATION_FAIL",
        [0x8054003F] = "MTP NO_THUMBNAIL",
        [0x80540040] = "MTP INCOMPLETE_TRANSFER",
        [0x80540041] = "MTP HOST_CANCELED",
        [0x80540042] = "MTP NEED_NASNE_UPDATE",
        [0x80540043] = "MTP NASNE_TERMINATED",
        [0x80540044] = "MTP NASNE_DISCONNECTED",
        [0x80540045] = "MTP CANT_PAUSE_OPERATION",
        [0x80540046] = "MTP PAUSED",
        [0x80540081] = "MTP UNKNOWN_OHFI_IN_ARRAY",
        [0x805400D0] = "MTP NET_ERROR",
        [0x805400D1] = "MTP NET_DISCONNECTED",
        [0x805400D2] = "MTP NET_NO_HOST_ASSIGNED",
        [0x805400D3] = "MTP NET_NO_HOST",
        [0x805400D4] = "MTP NET_HOST_BUSY",
        [0x805400D5] = "MTP NET_INVALID_RESPONSE",
        [0x805400D6] = "MTP NET_REGISTER_ALREADY",
        [0x805400D7] = "MTP NET_REGISTER_MAX_HOSTS",
        [0x805400D8] = "MTP NET_REGISTER_MAX_DEVICES",
        [0x805400D9] = "MTP NET_REGISTER_HOST_CANCELED",
        [0x805400DA] = "MTP NET_REGISTER_INVALID_PIN",
        [0x805400DB] = "MTP NET_REGISTER_HOST_TIMEOUT",
        [0x805400DC] = "MTP NET_REGISTER_ERROR",
        [0x805400E0] = "MTP NET_VERSION_LOW",
        [0x805400E1] = "MTP NET_NEED_PC_UPDATE",
        [0x805400E2] = "MTP NET_NEED_PS3_UPDATE",
        [0x805400E3] = "MTP NET_NEED_NASNE_UPDATE",
        [0x805400E4] = "MTP NET_INVALID_METHOD",
        [0x805400FD] = "MTP target device is not PC (only MtpHttpFile returns)",
        [0x805400FE] = "MTP target device is not PS3 (only MtpHttpFile returns)",
        [0x805400FF] = "MTP not supported",
        [0x80543601] = "MTP PHY ERROR",
        [0x80543602] = "MTP PHY ALREADY",
        [0x80543603] = "MTP PHY INVAL",
        [0x80543604] = "MTP PHY INPROGRESS",
        [0x80543605] = "MTP PHY TIMEOUT",
        [0x80543606] = "MTP PHY CANCEL",
        [0x80543607] = "MTP PHY DISCONNECT",
        [0x80543608] = "MTP PHY NOMEM",
        [0x80543609] = "MTP PHY NOTSUP",
        [0x8054360A] = "MTP PHY BREAK",
        [0x8054360B] = "MTP PHY PRIV_REQUIRED",
        [0x8054360C] = "MTP PHY NOTAVAIL",
        [0x8054360D] = "MTP PHY WAIT",
        [0x805436FF] = "MTP PHY FATAL",
        [0x80540101] = "MTP_SERVICE_ERROR_NOMEM",
        [0x80540102] = "MTP_SERVICE_ERROR_INVAL",
        [0x80540103] = "MTP_SERVICE_ERROR_BUSY",
        [0x80540104] = "MTP_SERVICE_ERROR_MAX_CLIENT",
        [0x80540105] = "MTP_SERVICE_ERROR_NOTFOUND",
        [0x80540106] = "MTP_SERVICE_ERROR_ALREADY",
        [0x80540107] = "MTP_SERVICE_ERROR_NOTINIT",
        [0x80540108] = "MTP_SERVICE_ERROR_CANCEL",
        [0x80540109] = "MTP_SERVICE_ERROR_TIMEOUT",
        [0x805401FF] = "MTP_SERVICE_ERROR_FATAL",
    };

    readonly Dictionary<string, uint> _strings = new();

    uint GuestString(string s)
    {
        lock (_strings)
        {
            if (_strings.TryGetValue(s, out uint p)) return p;
            byte[] b = Encoding.ASCII.GetBytes(s);
            p = Emu.Memory.Alloc((uint)b.Length + 1, "mtp client string", align: 4);
            b.CopyTo(GuestMemory.Span(p, b.Length));
            GuestMemory.Write<byte>(p + (uint)b.Length, 0);
            return _strings[s] = p;
        }
    }

    /// <summary>(this, int error) → const char *: the text of an MTP error (the Marlin ranges 0x805460xx-0x805464xx by
    /// group).</summary>
    [Hle("MtpClient::GetErrorString", Nid = 0xB31B725B, Library = Lib)]
    uint GetErrorString(uint me, uint code) => GuestString(ErrorString(code));

    internal static string ErrorString(uint code)
    {
        if (code == 0) return "Success";
        if (ErrorNames.TryGetValue(code, out var s)) return s;
        if (((code & 0xFFFFFFF) >> 16) == 0x54)
            switch ((code >> 8) & 0xFF)
            {
                case 0x60: return "MTP Marlin Mlndl error code";
                case 0x61: return "MTP Marlin Core error code";
                case 0x62: return "MTP Marlin Http error code";
                case 0x63: return "MTP Marlin MTP error code";
                case 0x64: return "MTP Marlin PS3 error code";
            }
        return "unknown error code";
    }

    // ---- status observer: +0 vtable, +4 client, +8 registered flag, +0xC first id (-1), +0x10 next id; the process's
    // registrations live in one list (+0 entries of 12 bytes, +4 count, +8 capacity)

    uint _statusList;

    uint StatusObserverVTable() => Stubs.VTable("StatusObserver", () =>
    [
        Stubs.Export(0xDD5400FF, Lib),
        Stubs.Host("StatusObserver::~StatusObserver(D0)", c => { uint o = c.Gpr(0); StatusObserverTerm(o); c.Return(o); }),
        Stubs.Host("StatusObserver::OnStatus", c => c.Return(0)),
    ]);

    uint BgObserverVTable() => Stubs.VTable("BgObserver", () =>
    [
        Stubs.Export(0x55751AC1, Lib),
        Stubs.Host("BgObserver::~BgObserver(D0)", c => { uint o = c.Gpr(0); BgObserverTerm(o); c.Return(o); }),
        Stubs.Host("BgObserver::OnEvent", c => c.Return(0)),
    ]);

    [Hle("MtpStatusObserver::MtpStatusObserver", Nid = 0x514EA2F6, Library = Lib)]
    uint StatusObserverCtor(uint me)
    {
        U(me, StatusObserverVTable());
        GuestMemory.Write<byte>(me + 8, 0);
        U(me + 4, 0);
        U(me + 0x10, 0);
        U(me + 0xC, uint.MaxValue);
        lock (_strings) _statusList = 0;
        return me;
    }

    /// <summary>(this): unregisters everything and frees the list.</summary>
    [Hle("MtpStatusObserver::~MtpStatusObserver", Nid = 0xDD5400FF, Library = Lib)]
    uint StatusObserverTerm(uint me)
    {
        StatusObserverUnregister(me, -1);
        lock (_strings)
        {
            if (_statusList != 0)
            {
                uint entries = U(_statusList);
                if (entries != 0) Emu.Memory.Free(entries);
                Emu.Memory.Free(_statusList);
                _statusList = 0;
            }
        }
        U(me + 4, 0);
        return me;
    }

    /// <summary>(this, MtpClient *client): binds the observer to a client and makes the registration list.</summary>
    [Hle("MtpStatusObserver::Initialize", Nid = 0xCF20935D, Library = Lib)]
    int StatusObserverInit(uint me, uint client)
    {
        if (client == 0) return INVAL;
        U(me + 4, client);
        lock (_strings)
        {
            if (_statusList != 0) return ALREADY;
            _statusList = Emu.Memory.Alloc(0x10, "mtp status observers", align: 4);
            GuestMemory.Fill(_statusList, 0, 0x10);
        }
        return 0;
    }

    /// <summary>(this, int id): removes a registration (-1: all, and the client's status listener).</summary>
    [Hle("MtpStatusObserver::Unregister", Nid = 0x33DE3302, Library = Lib)]
    int StatusObserverUnregister(uint me, int id)
    {
        uint client = U(me + 4);
        lock (_strings)
        {
            if (client == 0 || _statusList == 0) return NOTINIT;
            if (id == -1)
            {
                uint entries = U(_statusList);
                if (entries != 0) { Emu.Memory.Free(entries); U(_statusList, 0); U(_statusList + 4, 0); U(_statusList + 8, 0); }
                int r = ClearStatusListener(client);
                U(me + 0x10, 0);
                return r;
            }
            uint e = U(_statusList), n = U(_statusList + 4);
            for (uint i = 0; i < n; i++)
            {
                if ((int)U(e + i * 12 + 8) != id) continue;
                GuestMemory.Copy(e + i * 12, e + (i + 1) * 12, (n - i - 1) * 12);
                U(_statusList + 4, n - 1);
                if (n == 1) ClearStatusListener(client);
                break;
            }
            return 0;
        }
    }

    /// <summary>(this, callback, void *arg): registers a status callback (reads the host status from the service
    /// first).</summary>
    [Hle("MtpStatusObserver::Register", Nid = 0x55E889EB, Library = Lib)]
    int StatusObserverRegister(uint me, uint cb, uint arg)
    {
        uint client = U(me + 4);
        return client == 0 ? NOTINIT : Connected(client);
    }

    // ---- background observer: +0 vtable, +4 callback, +8 client, +0xC argument

    [Hle("MtpBgObserver::MtpBgObserver", Nid = 0xF13F51AF, Library = Lib)]
    uint BgObserverCtor(uint me)
    {
        U(me, BgObserverVTable());
        U(me + 4, 0);
        U(me + 0xC, 0);
        U(me + 8, 0);
        return me;
    }

    [Hle("MtpBgObserver::~MtpBgObserver", Nid = 0x55751AC1, Library = Lib)]
    uint BgObserverTerm(uint me)
    {
        uint client = U(me + 8);
        if (client != 0)
        {
            ClearBgListener(client);
            U(me + 4, 0);
            U(me + 0xC, 0);
        }
        U(me + 8, 0);
        return me;
    }

    [Hle("MtpBgObserver::Initialize", Nid = 0x8A307D8B, Library = Lib)]
    int BgObserverInit(uint me, uint client)
    {
        if (client == 0) return INVAL;
        U(me + 8, client);
        return 0;
    }

    /// <summary>(this, callback, void *arg): becomes the client's background listener.</summary>
    [Hle("MtpBgObserver::Register", Nid = 0xA513AD28, Library = Lib)]
    int BgObserverRegister(uint me, uint cb, uint arg)
    {
        uint client = U(me + 8);
        if (client == 0) return NOTINIT;
        U(me + 4, cb);
        U(me + 0xC, arg);
        return SetBgListener(client, me);
    }
}
