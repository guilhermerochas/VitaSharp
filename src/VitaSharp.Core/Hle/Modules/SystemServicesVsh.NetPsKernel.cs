namespace VitaSharp.Core.Hle.Modules;

// SceNetPsForDriver (os0:kd/bootimage.skprx SceNetPs 3.74, read in the module, study only), the entries that undo a
// network driver's registration and the two that need no driver: the stack keeps the attached interfaces in a list,
// one packet hook and one kernel gateway (each in a slot); detaching an interface not in the list, or unregistering a
// hook or a gateway that is not the registered one, answers 0x80418101 (NULL too). The emulated network is the
// host's sockets (SceNet HLE): no network driver attaches an interface, a hook or a gateway, so the list and the slots
// stay empty. NID_E41C0794 is empty in the module (0).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceNetPs: an invalid argument, or a driver object that is not registered (0x80418101).</summary>
    const int NETPS_ERR_INVALID = unchecked((int)0x80418101);

    /// <summary>NID_F5788FF8 (SceNetPsForDriver)(ifnet *ifp): detaches an attached interface; none is attached.</summary>
    [Hle("NID_F5788FF8", Nid = 0xF5788FF8, Library = "SceNetPsForDriver")]
    int NetPsDetachInterface(uint interfaceObject)
    {
        return NETPS_ERR_INVALID;
    }

    /// <summary>NID_C000CA8F (SceNetPsForDriver)(hook *hook): unregisters the packet hook; none is registered.</summary>
    [Hle("NID_C000CA8F", Nid = 0xC000CA8F, Library = "SceNetPsForDriver")]
    int NetPsUnregisterHook(uint hook)
    {
        return NETPS_ERR_INVALID;
    }

    /// <summary>NID_8C4346CA (SceNetPsForDriver)(gateway *gateway): unregisters the kernel gateway (and closes its
    /// socket); none is registered.</summary>
    [Hle("NID_8C4346CA", Nid = 0x8C4346CA, Library = "SceNetPsForDriver")]
    int NetPsUnregisterGateway(uint gateway)
    {
        return NETPS_ERR_INVALID;
    }

    /// <summary>NID_E41C0794 (SceNetPsForDriver)(): empty in the module; 0.</summary>
    [Hle("NID_E41C0794", Nid = 0xE41C0794, Library = "SceNetPsForDriver")]
    int NetPsNop()
    {
        return 0;
    }
}
