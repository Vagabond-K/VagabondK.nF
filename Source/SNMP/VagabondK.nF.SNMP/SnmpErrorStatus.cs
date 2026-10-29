namespace VagabondK.nF.SNMP
{
    internal enum SnmpErrorStatus
    {
        NoError = 0,
        TooBig = 1,
        NoSuchName = 2,
        BadValue = 3,
        NoAccess = 6,
        WrongType = 7,
        WrongLength = 8,
        WrongEncoding = 9,
        WrongValue = 10,
        NoCreation = 11,
        NotWritable = 17
    }
}
